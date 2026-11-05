using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build Utility > DLC Store Export: pick a DlcStoreExportSettings asset and a releases folder, seed the settings
/// from the checked releases' products, validate and export the PlayFab / Apple / Google API payloads, then upload
/// them. The export covers every product in the settings asset; checking releases only decides which products get
/// added. Uploading runs Tools~/IAP UPLOADER/upload_all.py: each store can be uploaded on its own, and only the
/// checked items are sent.
/// </summary>
public sealed class DlcStoreExportWindow : EditorWindow
{
    const string IconPath = "Packages/com.arbelos.buildutility/Editor/DlcPump/DlcPumpIcon.png";

    [SerializeField] DlcStoreExportSettings settings;
    [SerializeField] DlcReleaseListView list = new DlcReleaseListView();
    [SerializeField] bool showSettings = true;
    [SerializeField] string exportId = "";
    [SerializeField] string report = "";
    [SerializeField] string uploadExport = "";
    [SerializeField] bool showUploadSettings, activateGoogle, publishPlayFab;
    [SerializeField] List<bool> platformFoldouts = new List<bool> { true, true, true };
    [SerializeField] DlcWaveSchedule waveSchedule;
    [SerializeField] List<string> selectedWaveIds = new List<string>();
    Vector2 scroll, reportScroll;

    // Upload state. Items are re-read from the export folder after a reload.
    Dictionary<string, List<DlcStoreUploader.Item>> items;
    readonly Dictionary<string, string> platformState = new Dictionary<string, string>();
    string itemsError, itemsFor;
    bool loadingItems, uploading, azureBusy, azureOk;
    string azureStatus = "Azure: not checked yet";
    System.Threading.CancellationTokenSource cancelSource;
    readonly object live = new object();
    readonly Queue<string> liveTail = new Queue<string>();
    readonly List<(string platform, string name, string status)> liveItems = new List<(string, string, string)>();
    readonly List<(string platform, string state)> livePlatforms = new List<(string, string)>();
    bool liveDirty;
    string runningLabel;

    static string PrefKey(string name) => "Arbelos.DlcStoreExport." + PlayerSettings.productGUID + "." + name;

    [MenuItem("Build Utility/DLC Store Export")]
    public static void Open() => GetWindow<DlcStoreExportWindow>("DLC Store Export");

    void OnEnable()
    {
        titleContent = new GUIContent("DLC Store Export", AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
        minSize = new Vector2(560, 520);
        if (!settings)
        {
            string path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PrefKey("settings"), ""));
            settings = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<DlcStoreExportSettings>(path);
        }
        list.Load();
        if (!waveSchedule)
        {
            string wavePath = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PrefKey("waves"), ""));
            waveSchedule = string.IsNullOrEmpty(wavePath) ? null : AssetDatabase.LoadAssetAtPath<DlcWaveSchedule>(wavePath);
        }
        if (settings) AzureAction("check");
    }

    void OnProjectChange() { list.Refresh(); Repaint(); }

    void Update()
    {
        if (!uploading) return;
        lock (live)
        {
            if (!liveDirty) return;
            liveDirty = false;
            foreach (var (platform, state) in livePlatforms) platformState[platform] = state;
            livePlatforms.Clear();
            foreach (var (platform, name, status) in liveItems)
                if (items != null && items.TryGetValue(platform, out var list))
                    foreach (var item in list.Where(i => i.Matches(name))) item.status = status;
            liveItems.Clear();
            report = $"Running {runningLabel}…\n" + string.Join("\n", liveTail);
        }
        reportScroll.y = float.MaxValue;
        Repaint();
    }

    /// <summary>The ID written into requests.json: the typed one, else the single checked release's ID, else a dated one.</summary>
    string EffectiveExportId()
    {
        if (!string.IsNullOrWhiteSpace(exportId)) return exportId.Trim();
        var picked = list.Checked;
        return picked.Count == 1 ? picked[0].releaseId : "store-export-" + DateTime.UtcNow.ToString("yyyyMMdd");
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(uploading || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            try
            {
                EditorGUILayout.LabelField("DLC Store Export", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 }, GUILayout.Height(24));
                EditorGUILayout.HelpBox(
                    "Exports API payloads for every product in the store export settings: PlayFab Economy v2 drafts, Apple in-app purchases " +
                    "and Google Play one-time products, then uploads them. Check releases and use 'Add missing products' to put their products " +
                    "into the settings. Exporting needs no content build and sends no API requests; Upload sends them, store by store or all at once.",
                    MessageType.Info);
                DrawSettings();
                EditorGUILayout.Space();
                string listMessage = list.Draw();
                if (listMessage != null) report = listMessage;
                EditorGUILayout.Space();
                DrawActions();
                EditorGUILayout.Space();
                DrawUpload();
            }
            catch (Exception e) { report = e.Message; EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
        }
        EditorGUILayout.Space();
        if (uploading)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Running {runningLabel}…", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(cancelSource == null || cancelSource.IsCancellationRequested))
                    if (GUILayout.Button("Cancel", GUILayout.Width(70))) cancelSource?.Cancel();
            }
        EditorGUILayout.LabelField("Report", EditorStyles.boldLabel);
        reportScroll = EditorGUILayout.BeginScrollView(reportScroll, GUILayout.MinHeight(160));
        EditorGUILayout.SelectableLabel(report, EditorStyles.textArea, GUILayout.ExpandHeight(true),
            GUILayout.MinHeight(Mathf.Max(150, EditorStyles.textArea.CalcHeight(new GUIContent(report), position.width - 40))));
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndScrollView();
    }

    void DrawSettings()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            settings = (DlcStoreExportSettings)EditorGUILayout.ObjectField("Store export settings", settings, typeof(DlcStoreExportSettings), false);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PrefKey("settings"), settings ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)) : "");
                if (settings) AzureAction("check");
            }
            if (GUILayout.Button("New", GUILayout.Width(55))) CreateSettings();
        }
        if (!settings) { EditorGUILayout.HelpBox("Select or create a DlcStoreExportSettings asset.", MessageType.Warning); return; }

        EditorGUILayout.LabelField("Export scope", $"All {settings.options?.products?.Count ?? 0} products in {settings.name}");
        showSettings = EditorGUILayout.Foldout(showSettings, "Settings (platforms, app IDs, products, prices, localizations)", true);
        if (!showSettings) return;
        using (new EditorGUI.IndentLevelScope())
        {
            var so = new SerializedObject(settings);
            so.Update();
            EditorGUILayout.PropertyField(so.FindProperty("options"), true);
            so.ApplyModifiedProperties();
        }
    }

    void CreateSettings()
    {
        string path = EditorUtility.SaveFilePanelInProject("Store export settings", "DLC Store Export Settings", "asset", "Save in an Editor folder, outside Resources.");
        if (string.IsNullOrEmpty(path)) return;
        if (path.Split('/').Contains("Resources")) { report = "Store export settings must be outside Resources."; return; }
        var created = CreateInstance<DlcStoreExportSettings>();
        AssetDatabase.CreateAsset(created, path);
        AssetDatabase.SaveAssets();
        settings = created;
        EditorPrefs.SetString(PrefKey("settings"), AssetDatabase.AssetPathToGUID(path));
    }

    void DrawActions()
    {
        EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);
        if (!settings) return;
        var productIds = list.CheckedProductIds;
        using (new EditorGUI.DisabledScope(productIds.Count == 0))
        {
            if (GUILayout.Button($"Add missing products from {list.Checked.Count} checked release(s) ({productIds.Count} products)"))
            {
                var manifest = DlcStoreCatalogExport.ManifestFor(DlcReleaseService.Catalog(), productIds, EffectiveExportId());
                int before = settings.options.products.Count;
                Undo.RecordObject(settings, "Add store export products");
                DlcStoreCatalogExport.AddMissingProducts(manifest, settings.options);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                report = $"Added {settings.options.products.Count - before} product(s); {productIds.Count - (settings.options.products.Count - before)} were already in the settings and were left unchanged.\n" +
                         "Fill in locales, prices, territories and application IDs, then validate.";
            }
        }

        exportId = EditorGUILayout.TextField(new GUIContent("Export ID", "Written into requests.json. Blank: the checked release's ID, or a dated ID."), exportId);
        EditorGUILayout.LabelField(" ", "Using: " + EffectiveExportId(), EditorStyles.miniLabel);

        DrawWaves();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Validate platform catalog export")) Validate();
            if (GUILayout.Button("Export platform catalogs (JSON)…")) Export();
        }
    }

    // ------------------------------------------------------------------------------------------------ release waves

    void DrawWaves()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("PlayFab release waves", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            waveSchedule = (DlcWaveSchedule)EditorGUILayout.ObjectField("Wave schedule", waveSchedule, typeof(DlcWaveSchedule), false);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PrefKey("waves"), waveSchedule ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(waveSchedule)) : "");
                selectedWaveIds.Clear();
            }
            if (GUILayout.Button("Edit waves", GUILayout.Width(80))) DlcArtistWindow.Open();
        }
        if (!waveSchedule)
        {
            EditorGUILayout.LabelField(" ", "Optional. Pick a schedule to set PlayFab start and end dates from its waves.", EditorStyles.wordWrappedMiniLabel);
            return;
        }
        var waves = (waveSchedule.waves ?? new List<DlcWave>()).Where(w => w != null).ToList();
        selectedWaveIds.RemoveAll(id => waves.All(w => w.id != id));
        if (waves.Count == 0) { EditorGUILayout.HelpBox("This schedule has no waves yet. Add them in the Release Waves window.", MessageType.Info); return; }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            var now = DateTime.UtcNow;
            foreach (var wave in waves.OrderBy(w => w.starts, StringComparer.Ordinal))
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool on = selectedWaveIds.Contains(wave.id);
                    bool ticked = EditorGUILayout.ToggleLeft(new GUIContent(wave.name, string.Join(", ", wave.productIds)), on, GUILayout.MinWidth(140));
                    if (ticked != on) { if (ticked) selectedWaveIds.Add(wave.id); else selectedWaveIds.Remove(wave.id); }
                    string state;
                    try { state = DlcWaveExport.State(wave, waveSchedule.timeZone, now); } catch { state = "Needs dates"; }
                    EditorGUILayout.LabelField($"{DlcWaveExport.Describe(wave)}  ({waveSchedule.timeZone})", EditorStyles.miniLabel, GUILayout.MinWidth(200));
                    EditorGUILayout.LabelField($"{(wave.promotion ? "promotion" : "release")} · {state} · {wave.productIds.Count} products",
                        EditorStyles.miniLabel, GUILayout.Width(190));
                }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"{selectedWaveIds.Count} of {waves.Count} waves selected", EditorStyles.miniLabel);
            if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(45))) selectedWaveIds.Clear();
            if (GUILayout.Button("Validate waves", EditorStyles.miniButton, GUILayout.Width(100)))
            {
                var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), settings.options, EffectiveExportId());
                WavesForExport(manifest, out var errors, requireSelection: true);
                report = errors.Count == 0
                    ? $"PASS: the schedule has no overlapping release windows, and the {selectedWaveIds.Count} selected wave(s) can be exported together."
                    : $"{errors.Count} wave problem(s):\n" + string.Join("\n", errors);
            }
        }
        if (selectedWaveIds.Count > 0)
            EditorGUILayout.HelpBox("Exported PlayFab items of the selected waves get their start and end dates (and are hidden while a wave is paused). " +
                                    "Other products keep the dates they already have in PlayFab when they're uploaded. Dates take effect when the item is published.",
                                    MessageType.None);
    }

    /// <summary>The selected waves as a schedule document for the export, or null when none are selected.</summary>
    JObject WavesForExport(JObject manifest, out List<string> errors, bool requireSelection = false)
    {
        errors = new List<string>();
        if (!waveSchedule || (selectedWaveIds.Count == 0 && !requireSelection)) return null;
        var known = (DlcReleaseService.Catalog()["products"] as JArray ?? new JArray()).Where(p => (int?)p["type"] == 1).Select(p => (string)p["id"]).ToList();
        var exported = new HashSet<string>(((JArray)manifest["products"]).Select(p => (string)p["id"]), StringComparer.Ordinal);
        errors.AddRange(DlcWaveExport.Validate(waveSchedule, known));
        errors.AddRange(DlcWaveExport.ValidateSelection(waveSchedule, selectedWaveIds, exported));
        if (!settings.options.playFab) errors.Add("Release waves set PlayFab dates, but PlayFab is turned off in the store export settings.");
        if (errors.Count > 0) return null;
        return DlcWaveExport.Build(waveSchedule, known, selectedWaveIds);
    }

    void Validate()
    {
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), settings.options, EffectiveExportId());
        var errors = DlcStoreCatalogExport.Validate(manifest, settings.options);
        WavesForExport(manifest, out var waveErrors);
        errors.AddRange(waveErrors);
        report = errors.Count == 0
            ? $"PASS: required export inputs are complete for all {((JArray)manifest["products"]).Count} products" +
              (selectedWaveIds.Count > 0 && waveSchedule ? $", and the {selectedWaveIds.Count} selected release wave(s) are valid" : "") +
              ". Apple response bindings are resolved at upload time."
            : $"{errors.Count} problem(s):\n" + string.Join("\n", errors);
    }

    void Export()
    {
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), settings.options, EffectiveExportId());
        var errors = DlcStoreCatalogExport.Validate(manifest, settings.options);
        var waves = WavesForExport(manifest, out var waveErrors);
        errors.AddRange(waveErrors);
        if (errors.Count > 0) { report = "Export blocked:\n" + string.Join("\n", errors); return; }
        string last = EditorPrefs.GetString(PrefKey("parent"), "");
        string parent = EditorUtility.OpenFolderPanel("Choose parent for a new store export folder", Directory.Exists(last) ? last : "", "");
        if (string.IsNullOrEmpty(parent)) return;
        EditorPrefs.SetString(PrefKey("parent"), parent);
        AssetDatabase.SaveAssets();
        string output = DlcStoreCatalogExport.Export(manifest, settings.options, parent, DlcAppleScreenshot.Prepare, waves);
        int dated = waves == null ? 0 : (waves["waves"] as JArray).SelectMany(w => w["productIds"]).Select(t => (string)t).Distinct().Count();
        report = $"Exported all {((JArray)manifest["products"]).Count} configured products, ordered requests, validation report and instructions to {output}\n" +
                 (waves != null ? $"PlayFab dates set from {selectedWaveIds.Count} release wave(s) for {dated} product(s); see release-waves.json.\n" : "") +
                 "It's selected in Upload below.";
        uploadExport = output;
        LoadItems();
    }

    // ------------------------------------------------------------------------------------------------ upload

    void DrawUpload()
    {
        EditorGUILayout.LabelField("Upload", EditorStyles.boldLabel);
        if (!settings) return;

        showUploadSettings = EditorGUILayout.Foldout(showUploadSettings,
            $"Upload settings (Key Vault: {(string.IsNullOrWhiteSpace(settings.keyVaultName) ? "not set" : settings.keyVaultName)})", true);
        if (showUploadSettings)
            using (new EditorGUI.IndentLevelScope())
            {
                var so = new SerializedObject(settings);
                so.Update();
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(so.FindProperty("keyVaultName"));
                EditorGUILayout.PropertyField(so.FindProperty("azureTenant"));
                EditorGUILayout.PropertyField(so.FindProperty("pythonExecutable"));
                bool changed = EditorGUI.EndChangeCheck();
                so.ApplyModifiedProperties();
                if (changed) AssetDatabase.SaveAssets();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(" ", "Logs: " + DlcStoreUploader.LogDir, EditorStyles.miniLabel);
                    if (GUILayout.Button("Install Python packages", GUILayout.Width(170))) InstallPackages();
                }
            }

        DrawAzure();

        // Export folder: the ones under the folder exports were last written to, newest first.
        string parent = EditorPrefs.GetString(PrefKey("parent"), "");
        if (!string.IsNullOrEmpty(uploadExport) && Directory.Exists(uploadExport))
        {
            uploadExport = Path.GetFullPath(uploadExport);
            parent = Path.GetDirectoryName(uploadExport);
        }
        var exports = DlcStoreUploader.Exports(parent);
        if (!string.IsNullOrEmpty(uploadExport) && Directory.Exists(uploadExport) && !exports.Contains(uploadExport)) exports.Insert(0, uploadExport);
        using (new EditorGUILayout.HorizontalScope())
        {
            int index = exports.IndexOf(uploadExport);
            string[] labels = exports.Select(Path.GetFileName).ToArray();
            int picked = exports.Count == 0 ? -1 : EditorGUILayout.Popup("Export", Math.Max(0, index), labels);
            if (exports.Count == 0) EditorGUILayout.LabelField("Export", "(none yet: export above, or Browse)");
            if (picked >= 0 && exports[picked] != uploadExport) { uploadExport = exports[picked]; LoadItems(); }
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string chosen = EditorUtility.OpenFolderPanel("A dlc-store-export folder", Directory.Exists(parent) ? parent : "", "");
                if (!string.IsNullOrEmpty(chosen)) { uploadExport = Path.GetFullPath(chosen); LoadItems(); }
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(uploadExport) || loadingItems))
                if (GUILayout.Button("Reload", GUILayout.Width(60))) LoadItems();
        }
        if (string.IsNullOrEmpty(uploadExport) || !Directory.Exists(uploadExport)) return;
        if (items == null && !loadingItems && itemsFor != uploadExport) LoadItems();
        if (loadingItems) { EditorGUILayout.HelpBox("Reading the export…", MessageType.None); return; }
        if (itemsError != null) { EditorGUILayout.HelpBox(itemsError, MessageType.Error); return; }
        if (items == null) return;

        activateGoogle = EditorGUILayout.ToggleLeft(new GUIContent("Google Play: make purchase options active (--activate)",
            "Off: purchase options are left inactive and can be activated in Play Console."), activateGoogle);
        publishPlayFab = EditorGUILayout.ToggleLeft(new GUIContent("PlayFab: publish the items (--publish)",
            "Off: items are created or updated as drafts, to review in the DLC PlayFab Review window or Game Manager."), publishPlayFab);

        for (int i = 0; i < DlcStoreUploader.Platforms.Length; i++) DrawPlatform(i);

        int checkedTotal = DlcStoreUploader.Platforms.Sum(p => Selected(p).Count);
        EditorGUILayout.Space(2);
        using (new EditorGUI.DisabledScope(checkedTotal == 0))
        using (new EditorGUILayout.HorizontalScope())
        {
            var all = DlcStoreUploader.Platforms.Where(p => Selected(p).Count > 0).ToArray();
            if (GUILayout.Button($"Dry run all checked ({checkedTotal})")) StartUpload(all, true);
            if (GUILayout.Button($"Upload all checked ({checkedTotal})…")) StartUpload(all, false);
        }
        EditorGUILayout.LabelField("Stores run in the order Apple, Google Play, PlayFab; the first failure stops the run. Existing items are updated.", EditorStyles.miniLabel);
    }

    void DrawAzure()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            var style = new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold };
            style.normal.textColor = azureBusy ? style.normal.textColor
                : azureOk ? new Color(0.10f, 0.55f, 0.25f) : new Color(0.80f, 0.15f, 0.20f);
            EditorGUILayout.LabelField(azureStatus, style);
            if (DlcAzureCli.SigningIn)
            {
                if (GUILayout.Button("Cancel sign-in", GUILayout.Width(115))) DlcAzureCli.CancelLogin();
            }
            else using (new EditorGUI.DisabledScope(azureBusy))
            {
                if (GUILayout.Button("Sign in to Azure", GUILayout.Width(115))) AzureAction("login");
                if (GUILayout.Button("Check again", GUILayout.Width(85))) AzureAction("check");
                if (GUILayout.Button("Sign out", GUILayout.Width(65))) AzureAction("logout");
            }
        }
    }

    async void AzureAction(string action)
    {
        if (azureBusy || !settings) return;
        azureBusy = true;
        azureStatus = action == "login" ? "Azure: finish signing in in your browser (it may open behind Unity)…" : "Azure: checking…";
        Repaint();
        try
        {
            if (action == "login")
            {
                var (ok, message) = await DlcAzureCli.Login(settings.azureTenant);
                report = ok ? "Signed in to Azure." : "Azure sign-in failed:\n" + message;
            }
            else if (action == "logout")
            {
                await DlcAzureCli.Logout();
                report = "Signed out of Azure.";
            }
            var (user, reason) = await DlcAzureCli.Account();
            if (user == null) { azureOk = false; azureStatus = "Azure: " + reason; }
            else
            {
                azureStatus = $"Azure: signed in as {user}   |   checking the Key Vault…";
                Repaint();
                var (ok, message) = await DlcStoreUploader.CheckVault(settings.pythonExecutable, settings.keyVaultName);
                azureOk = ok;
                azureStatus = $"Azure: signed in as {user}   |   {message}";
            }
        }
        catch (Exception e) { azureOk = false; azureStatus = "Azure: check failed: " + e.Message; }
        finally
        {
            azureBusy = false;
            if (this) Repaint();
        }
    }

    async void LoadItems()
    {
        if (!settings || string.IsNullOrEmpty(uploadExport) || loadingItems) return;
        loadingItems = true;
        itemsFor = uploadExport;
        itemsError = null;
        platformState.Clear();
        string export = uploadExport;
        try
        {
            var (loaded, error) = await DlcStoreUploader.List(settings.pythonExecutable, export);
            if (export != uploadExport) return; // picked another export meanwhile
            items = loaded;
            itemsError = error;
        }
        catch (Exception e) { items = null; itemsError = e.Message; }
        finally
        {
            loadingItems = false;
            if (this) Repaint();
        }
    }

    List<DlcStoreUploader.Item> Items(string platform) =>
        items != null && items.TryGetValue(platform, out var list) ? list : new List<DlcStoreUploader.Item>();

    List<DlcStoreUploader.Item> Selected(string platform) => Items(platform).Where(i => i.selected && i.problem == null).ToList();

    void DrawPlatform(int index)
    {
        string platform = DlcStoreUploader.Platforms[index];
        var list = Items(platform);
        var chosen = Selected(platform);
        while (platformFoldouts.Count <= index) platformFoldouts.Add(true);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                platformFoldouts[index] = EditorGUILayout.Foldout(platformFoldouts[index],
                    $"{DlcStoreUploader.Labels[platform]}  ({chosen.Count} of {list.Count} checked)", true, EditorStyles.foldoutHeader);
                if (platformState.TryGetValue(platform, out string state)) StatusLabel(state, 110);
                using (new EditorGUI.DisabledScope(list.Count == 0))
                {
                    if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(36))) foreach (var i in list) i.selected = i.problem == null;
                    if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(42))) foreach (var i in list) i.selected = false;
                }
                using (new EditorGUI.DisabledScope(chosen.Count == 0))
                {
                    if (GUILayout.Button("Dry run", GUILayout.Width(65))) StartUpload(new[] { platform }, true);
                    if (GUILayout.Button("Upload…", GUILayout.Width(70))) StartUpload(new[] { platform }, false);
                }
            }
            if (!platformFoldouts[index]) return;
            if (list.Count == 0) { EditorGUILayout.LabelField("No items for this store in the export.", EditorStyles.miniLabel); return; }
            foreach (var item in list)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(item.problem != null))
                        item.selected = EditorGUILayout.Toggle(item.selected && item.problem == null, GUILayout.Width(16));
                    EditorGUILayout.LabelField(new GUIContent(item.key, item.id), EditorStyles.boldLabel, GUILayout.MinWidth(160));
                    EditorGUILayout.LabelField(new GUIContent(item.name, item.name), GUILayout.MinWidth(90));
                    if (item.problem != null)
                    {
                        var red = new GUIStyle(EditorStyles.miniLabel);
                        red.normal.textColor = new Color(0.85f, 0.2f, 0.2f);
                        EditorGUILayout.LabelField(new GUIContent(item.problem, item.problem), red, GUILayout.MinWidth(120));
                    }
                    else EditorGUILayout.LabelField(new GUIContent(item.details, item.details), EditorStyles.miniLabel, GUILayout.MinWidth(120));
                    StatusLabel(item.status, 95);
                }
        }
    }

    static void StatusLabel(string status, float width)
    {
        if (string.IsNullOrEmpty(status)) { GUILayout.Space(width); return; }
        var style = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleRight };
        bool bad = status.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;
        bool good = status == "Created" || status == "Updated" || status == "done" || status == "dry run OK";
        style.normal.textColor = bad ? new Color(0.85f, 0.2f, 0.2f) : good ? new Color(0.15f, 0.6f, 0.3f) : style.normal.textColor;
        EditorGUILayout.LabelField(status, style, GUILayout.Width(width));
    }

    void StartUpload(string[] platforms, bool dryRun)
    {
        // Per store: every item when all are checked (no filter), otherwise just the checked ones.
        var selection = new Dictionary<string, List<string>>();
        foreach (string p in platforms)
        {
            var chosen = Selected(p);
            if (chosen.Count == 0) continue;
            selection[p] = chosen.Count == Items(p).Count ? null : chosen.Select(i => i.arg).ToList();
        }
        if (selection.Count == 0) { report = "Nothing checked to upload."; return; }
        string summary = string.Join("\n", selection.Select(kv =>
            $"  {DlcStoreUploader.Labels[kv.Key]}: {(kv.Value == null ? $"all {Items(kv.Key).Count}" : kv.Value.Count.ToString())} item(s)"));
        if (!dryRun)
        {
            if (string.IsNullOrWhiteSpace(settings.keyVaultName)) { report = "Set the Key Vault name in Upload settings first."; showUploadSettings = true; return; }
            if (!EditorUtility.DisplayDialog("Upload to the stores",
                    $"Upload from\n{Path.GetFileName(uploadExport)}\n\n{summary}\n\n" +
                    (selection.ContainsKey("google") ? $"Google purchase options: {(activateGoogle ? "ACTIVE" : "left inactive")}\n" : "") +
                    (selection.ContainsKey("playfab") ? $"PlayFab items: {(publishPlayFab ? "PUBLISHED" : "drafts")}\n" : "") +
                    "\nExisting items are updated. The first failure stops the run.", "Upload", "Cancel"))
                return;
        }

        foreach (string p in selection.Keys)
        {
            platformState[p] = "waiting";
            foreach (var item in Items(p)) item.status = item.selected && item.problem == null ? "…" : "";
        }
        RunUpload(selection, dryRun, $"{(dryRun ? "dry run" : "upload")}: {string.Join(", ", selection.Keys.Select(k => DlcStoreUploader.Labels[k]))}");
    }

    async void RunUpload(Dictionary<string, List<string>> selection, bool dryRun, string label)
    {
        uploading = true;
        runningLabel = label;
        lock (live) { liveTail.Clear(); liveItems.Clear(); livePlatforms.Clear(); liveDirty = false; }
        cancelSource = new System.Threading.CancellationTokenSource();
        report = $"Running {label}…";
        Repaint();
        try
        {
            var (code, output) = await DlcStoreUploader.Upload(settings.pythonExecutable, uploadExport, settings.keyVaultName, selection,
                dryRun, activateGoogle, publishPlayFab,
                l => { lock (live) { liveTail.Enqueue(l); while (liveTail.Count > 200) liveTail.Dequeue(); liveDirty = true; } },
                (p, state) => { lock (live) { livePlatforms.Add((p, state)); liveDirty = true; } },
                (p, name, status) => { lock (live) { liveItems.Add((p, name, status)); liveDirty = true; } },
                cancelSource.Token);
            Update(); // apply the last statuses
            uploading = false;
            foreach (string p in selection.Keys)
            {
                if (platformState.TryGetValue(p, out string s) && (s == "waiting" || s == "running")) platformState[p] = code == 0 ? "done" : "not run";
                foreach (var item in Items(p).Where(i => i.status == "…"))
                    item.status = code == 0 ? (dryRun ? "checked" : "") : "";
            }
            report = (code == 0 ? "" : $"{label.ToUpperInvariant()} FAILED (exit {code})\n") + output;
            if (code == 0) Debug.Log("[IAP Upload]\n" + output); else Debug.LogError("[IAP Upload]\n" + output);
        }
        catch (Exception e) { report = $"{label} failed: {e}"; Debug.LogException(e); }
        finally
        {
            uploading = false;
            cancelSource.Dispose();
            cancelSource = null;
            if (this) { reportScroll = Vector2.zero; Repaint(); }
        }
    }

    async void InstallPackages()
    {
        uploading = true;
        runningLabel = "pip install";
        lock (live) { liveTail.Clear(); liveDirty = false; }
        cancelSource = new System.Threading.CancellationTokenSource();
        try
        {
            var (code, output) = await DlcStoreUploader.InstallPackages(settings.pythonExecutable,
                l => { lock (live) { liveTail.Enqueue(l); while (liveTail.Count > 200) liveTail.Dequeue(); liveDirty = true; } }, cancelSource.Token);
            report = (code == 0 ? "Python packages are installed.\n" : $"pip install FAILED (exit {code})\n") + output;
        }
        catch (Exception e) { report = "pip install failed: " + e.Message; }
        finally
        {
            uploading = false;
            cancelSource.Dispose();
            cancelSource = null;
            if (this) Repaint();
        }
    }
}
