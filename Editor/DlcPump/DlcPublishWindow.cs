using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build Utility > DLC Publish: pick publishing settings and a releases folder, check releases, build Addressables
/// (full or content update, with a suggestion from what's already published), export their per-product manifests,
/// then publish the export with Tools~/DlcPublisher/publish_dlc.py.
/// </summary>
public sealed class DlcPublishWindow : EditorWindow
{
    const string IconPath = "Packages/com.arbelos.buildutility/Editor/DlcPump/DlcPumpIcon.png";

    [SerializeField] DlcPublishingSettings settings;
    [SerializeField] DlcReleaseListView list = new DlcReleaseListView();
    [SerializeField] bool showSettings = true, showRollback, onlyCheckedProducts = true, rollbackProduction, promoteAllowNewCatalog;
    [SerializeField] string report = "";
    [SerializeField] string rollbackProduct = "", rollbackVersion = "v1";
    [SerializeField] int rollbackMode;  // 0 = make current, 1 = drop
    [SerializeField] int exportIndex;
    [SerializeField] string baselinePath = "";
    [SerializeField] bool showBuildTools;

    /// <summary>The last real publish, so a failed or cancelled one can be continued with the same arguments.</summary>
    [Serializable]
    sealed class PublishJob
    {
        public string folder;
        public List<string> products; // null = every product in the export
        public bool allowNewCatalog, hasProducts;
        public bool failed;
        public string what;
    }

    [SerializeField] PublishJob lastPublish;

    List<string> exports = new List<string>();
    Vector2 scroll, reportScroll;
    bool busy;

    // Live output of the running command, filled from the script's background threads.
    const int LiveLines = 200;
    readonly object live = new object();
    readonly Queue<string> liveTail = new Queue<string>();
    DlcPublishProgress liveProgress, shownProgress;
    bool liveDirty;
    string runningLabel;
    System.Threading.CancellationTokenSource cancelSource;
    bool azureBusy, azureOk;
    bool allowNewCatalog; // deliberately not serialized: off again after a reload
    string azureStatus = "Azure: not checked yet";

    // What's published for the current platform and app version, for the build suggestion.
    DlcContentPublisher.RemoteVersionState remote;
    string remoteError, remoteKey;
    bool remoteBusy, downloadingBaseline;

    static string VersionKey => DlcContentPublisher.CurrentPlatform + "/" + DlcContentPublisher.CurrentAppVersion;
    bool Busy => busy || DlcAddressablesBuild.Building;

    static string PrefKey(string name) => "Arbelos.DlcPublish." + PlayerSettings.productGUID + "." + name;

    /// <summary>The publishing settings last chosen in this window, for other DLC windows.</summary>
    public static DlcPublishingSettings SavedSettings()
    {
        string path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PrefKey("settings"), ""));
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<DlcPublishingSettings>(path);
    }

    [MenuItem("Build Utility/DLC Publish")]
    public static void Open() => GetWindow<DlcPublishWindow>("DLC Publish");

    void OnEnable()
    {
        titleContent = new GUIContent("DLC Publish", AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
        minSize = new Vector2(560, 520);
        if (!settings)
        {
            string path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PrefKey("settings"), ""));
            settings = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<DlcPublishingSettings>(path);
        }
        list.Load(PrefKey("folder"));
        RefreshExports();
        AzureAction("check");
    }

    void OnProjectChange() { list.Refresh(); RefreshExports(); Repaint(); }

    void Update()
    {
        if (!busy) return;
        lock (live)
        {
            if (!liveDirty) return;
            liveDirty = false;
            shownProgress = liveProgress;
            report = $"Running {runningLabel}…\n" + string.Join("\n", liveTail);
        }
        reportScroll.y = float.MaxValue; // follow the output
        Repaint();
    }
    void OnFocus() => RefreshExports();

    void RefreshExports()
    {
        exports = settings ? DlcContentPublisher.Exports(settings) : new List<string>();
        exportIndex = Mathf.Clamp(exportIndex, 0, Math.Max(0, exports.Count - 1));
    }

    List<DlcRelease> Checked() => list.Checked;

    void OnGUI()
    {
        if (remoteKey != VersionKey)
        {
            // Switched platform or app version: the remote state and baseline belong to the old one.
            if (remoteKey != null) baselinePath = "";
            remoteKey = VersionKey;
            remote = null;
            remoteError = null;
            if (azureOk) RefreshRemote();
        }
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(Busy || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            DrawHeader();
            DrawSettings();
            EditorGUILayout.Space();
            string listMessage = list.Draw();
            if (listMessage != null) report = listMessage;
            EditorGUILayout.Space();
            DrawBuild();
            EditorGUILayout.Space();
            DrawExport();
            EditorGUILayout.Space();
            DrawPublish();
            EditorGUILayout.Space();
            DrawPromote();
            EditorGUILayout.Space();
            DrawRollback();
        }
        EditorGUILayout.Space();
        DrawProgress();
        EditorGUILayout.LabelField(busy ? "Running…" : "Report", EditorStyles.boldLabel);
        reportScroll = EditorGUILayout.BeginScrollView(reportScroll, GUILayout.MinHeight(160));
        EditorGUILayout.SelectableLabel(report, EditorStyles.textArea, GUILayout.ExpandHeight(true),
            GUILayout.MinHeight(Mathf.Max(150, EditorStyles.textArea.CalcHeight(new GUIContent(report), position.width - 40))));
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndScrollView();
    }

    void DrawHeader()
    {
        EditorGUILayout.LabelField("DLC Publish", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 }, GUILayout.Height(24));
        EditorGUILayout.HelpBox(
            "1. Check releases, then Build (full or content update, as suggested).  2. Export.  3. Publish to STAGING (dry run first).  " +
            "4. Test with a staging build or the staging Play Mode.  5. Promote to production.\n" +
            "Uploads go to <container>/<Platform>/<appVersion>/ as the Azure user signed in below (Storage Blob Data Contributor). No keys are stored. See Documentation~/DLC-CONTENT.md.",
            MessageType.Info);
    }

    void DrawSettings()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            settings = (DlcPublishingSettings)EditorGUILayout.ObjectField("Publishing settings", settings, typeof(DlcPublishingSettings), false);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(PrefKey("settings"), settings ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)) : "");
                RefreshExports();
                AzureAction("check");
            }
            if (GUILayout.Button("New", GUILayout.Width(55))) CreateSettings();
        }
        if (!settings) { EditorGUILayout.HelpBox("Select or create a DlcPublishingSettings asset.", MessageType.Warning); return; }

        showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true);
        if (showSettings)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                var so = new SerializedObject(settings);
                so.Update();
                var p = so.GetIterator();
                for (bool enter = true; p.NextVisible(enter); enter = false)
                    if (p.name != "m_Script") EditorGUILayout.PropertyField(p, true);
                if (so.ApplyModifiedProperties()) RefreshExports();
            }
        }
        EditorGUILayout.LabelField("Platform / app version", $"{DlcContentPublisher.CurrentPlatform}  /  {DlcContentPublisher.CurrentAppVersion}");
        EditorGUILayout.LabelField("Staging (Publish)", DlcContentPublisher.Destination(settings), EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("Production (Promote)", DlcContentPublisher.ProductionDestination(settings), EditorStyles.wordWrappedLabel);
        DrawAzure();
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
        if (azureBusy) return;
        azureBusy = true;
        azureStatus = action == "login" ? "Azure: finish signing in in your browser (it may open behind Unity)…" : "Azure: checking…";
        Repaint();
        try
        {
            if (action == "login")
            {
                var (ok, message) = await DlcAzureCli.Login(settings ? settings.azureTenant : "");
                report = ok ? "Signed in to Azure." : "Azure sign-in failed:\n" + message;
            }
            else if (action == "logout")
            {
                await DlcAzureCli.Logout();
                report = "Signed out of Azure.";
            }

            var (user, reason) = await DlcAzureCli.Account();
            if (user == null) { azureOk = false; azureStatus = "Azure: " + reason; }
            else if (!settings || string.IsNullOrWhiteSpace(settings.accountName))
            {
                azureOk = false;
                azureStatus = $"Azure: signed in as {user}   |   set accountName in the publishing settings";
            }
            else
            {
                azureStatus = $"Azure: signed in as {user}   |   checking {settings.accountName}/{settings.container}…";
                Repaint();
                var (code, output) = await DlcContentPublisher.Check(settings);
                azureOk = code == 0;
                string error = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error:"));
                azureStatus = azureOk
                    ? $"Azure: signed in as {user}   |   can read {settings.accountName}/{settings.container}"
                    : $"Azure: signed in as {user}   |   no access: {(error ?? output.Trim()).Replace("error: ", "")}";
                if (azureOk) RefreshRemote();
            }
        }
        catch (Exception e) { azureOk = false; azureStatus = "Azure: check failed: " + e.Message; }
        finally
        {
            azureBusy = false;
            if (this) Repaint();
        }
    }

    void CreateSettings()
    {
        string path = EditorUtility.SaveFilePanelInProject("Publishing settings", "DLC Publishing Settings", "asset", "Save in an Editor folder, outside Resources.");
        if (string.IsNullOrEmpty(path)) return;
        if (path.Split('/').Contains("Resources")) { report = "Publishing settings must be outside Resources."; return; }
        var created = CreateInstance<DlcPublishingSettings>();
        AssetDatabase.CreateAsset(created, path);
        AssetDatabase.SaveAssets();
        settings = created;
        EditorPrefs.SetString(PrefKey("settings"), AssetDatabase.AssetPathToGUID(path));
        RefreshExports();
    }

    async void RefreshRemote()
    {
        if (!settings || remoteBusy) return;
        remoteBusy = true;
        string key = VersionKey;
        try
        {
            var (state, error) = await DlcContentPublisher.RemoteState(settings);
            if (key != VersionKey) return; // switched platform meanwhile; OnGUI asks again
            remote = state;
            remoteError = error;
        }
        catch (Exception e) { remote = null; remoteError = e.Message; }
        finally
        {
            remoteBusy = false;
            if (this) Repaint();
        }
    }

    void DrawBuild()
    {
        EditorGUILayout.LabelField("Build", EditorStyles.boldLabel);
        string pv = $"{DlcContentPublisher.CurrentPlatform} {DlcContentPublisher.CurrentAppVersion}";
        int suggested = 0; // 1 = full build, 2 = content update
        string suggestion;
        var type = MessageType.Info;
        if (!DlcReleaseService.IsStorePlatform(EditorUserBuildSettings.activeBuildTarget))
        {
            suggestion = $"The active build target is {EditorUserBuildSettings.activeBuildTarget}. Switch to Android or iOS; every release builds for both.";
            type = MessageType.Warning;
        }
        else if (remoteBusy) suggestion = $"Checking what's published for {pv}…";
        else if (remote == null)
        {
            suggestion = remoteError != null ? $"Couldn't read what's published for {pv}: {remoteError}" : "Sign in to Azure above to get a build suggestion.";
            if (remoteError != null) type = MessageType.Warning;
        }
        else if (!remote.HasCatalog)
        {
            suggested = 1;
            suggestion = $"Suggested: FULL BUILD. Nothing is published for {pv} yet.";
        }
        else
        {
            suggested = 2;
            suggestion = $"Suggested: CONTENT UPDATE. {pv} already has {string.Join(", ", remote.catalogs)} ({remote.products} products), " +
                         "and installed players only load that catalog. " +
                         (remote.baseline ? "Its baseline is stored in Azure: click Download from Azure."
                                          : "No baseline is stored for it: choose the .bin from the full build players have.");
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.HelpBox(suggestion, type);
            using (new EditorGUI.DisabledScope(!settings || !azureOk || remoteBusy))
                if (GUILayout.Button("Check again", GUILayout.Width(85), GUILayout.Height(38))) RefreshRemote();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            baselinePath = EditorGUILayout.TextField(new GUIContent("Content-state baseline",
                "addressables_content_state.bin from the full build players have. Only used by Content update."), baselinePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string chosen = EditorUtility.OpenFilePanel("Content state of the full build players have", "", "bin");
                if (!string.IsNullOrEmpty(chosen)) baselinePath = chosen;
            }
            using (new EditorGUI.DisabledScope(!settings || downloadingBaseline || (remote != null && !remote.baseline)))
                if (GUILayout.Button(new GUIContent(downloadingBaseline ? "Downloading…" : "Download from Azure",
                        $"Downloads the baseline stored when the full build of {pv} was published."), GUILayout.Width(140)))
                    DownloadBaseline();
        }

        var picked = Checked();
        string profileError = picked.Count > 0 ? DlcAddressablesBuild.CheckProfile(picked, out _) : null;
        if (profileError != null) EditorGUILayout.HelpBox(profileError, MessageType.Warning);
        string loadPathWarning = DlcAddressablesBuild.LoadPathWarning();
        if (loadPathWarning != null)
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(loadPathWarning, MessageType.Warning);
                if (GUILayout.Button("Use runtime\ncontent URL", GUILayout.Width(110), GUILayout.Height(52)) &&
                    EditorUtility.DisplayDialog("Use the runtime content URL",
                        $"Set Remote.LoadPath of the active Addressables profile to\n{Arbelos.BuildUtility.Runtime.DlcEnvironment.RemoteLoadPath}?\n\n" +
                        "Only for an app version that hasn't shipped with the fixed URL. Build again (full build for a new app version) afterwards.",
                        "Change it", "Cancel"))
                {
                    try { report = "Remote.LoadPath was: " + DlcAddressablesBuild.UseRuntimeLoadPath() + "\nnow: " + Arbelos.BuildUtility.Runtime.DlcEnvironment.RemoteLoadPath; }
                    catch (Exception e) { report = "Couldn't change Remote.LoadPath: " + e.Message; }
                }
            }
        using (new EditorGUI.DisabledScope(picked.Count == 0 || profileError != null))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (BuildButton("Full build", suggested == 1)) StartBuild(false);
            if (BuildButton("Content update", suggested == 2)) StartBuild(true);
        }
        if (picked.Count == 0)
            EditorGUILayout.LabelField("Check the releases this build is for. A build always covers every Addressables group.", EditorStyles.miniLabel);

        showBuildTools = EditorGUILayout.Foldout(showBuildTools, "Build tools", true);
        if (showBuildTools) using (new EditorGUI.IndentLevelScope()) DrawBuildTools(picked);
    }

    static bool BuildButton(string label, bool suggested)
    {
        var old = GUI.backgroundColor;
        if (suggested) GUI.backgroundColor = new Color(0.45f, 0.85f, 0.5f);
        bool clicked = GUILayout.Button(suggested ? label + "  (suggested)" : label, GUILayout.Height(24));
        GUI.backgroundColor = old;
        return clicked;
    }

    void StartBuild(bool update)
    {
        string pv = $"{DlcContentPublisher.CurrentPlatform} {DlcContentPublisher.CurrentAppVersion}";
        if (!update && remote != null && remote.HasCatalog && !EditorUtility.DisplayDialog("Full build of a published version?",
                $"{pv} already has a published catalog ({string.Join(", ", remote.catalogs)}).\n\nA full build makes a catalog with a new name. " +
                "Players who already have this version never load it, and publishing it is refused unless you allow a new catalog name.\n\n" +
                "Only do this if this version hasn't shipped to players. Otherwise use Content update.", "Full build anyway", "Cancel"))
            return;
        if (update && remote != null && !remote.HasCatalog && !EditorUtility.DisplayDialog("Content update of an unpublished version?",
                $"Nothing is published for {pv} yet, so there's no shipped build to update. A full build is usually what you want.",
                "Content update anyway", "Cancel"))
            return;

        string error = DlcAddressablesBuild.Queue(Checked(), update ? (baselinePath ?? "") : null, (ok, message) =>
        {
            report = message;
            if (this) Repaint();
        });
        report = error ?? $"{(update ? "Content update" : "Full build")} for {pv} queued; Unity is busy until it finishes. Receipts: {DlcAddressablesBuild.ReceiptFolder}";
        Repaint();
    }

    async void DownloadBaseline()
    {
        downloadingBaseline = true;
        report = $"Downloading the content-state baseline for {DlcContentPublisher.CurrentPlatform} {DlcContentPublisher.CurrentAppVersion}…";
        try
        {
            var (code, output) = await DlcContentPublisher.DownloadBaseline(settings);
            if (code == 0) { baselinePath = DlcContentPublisher.BaselineDownloadPath; report = output; }
            else report = "BASELINE DOWNLOAD FAILED\n" + output;
        }
        catch (Exception e) { report = "Baseline download failed: " + e.Message; }
        finally
        {
            downloadingBaseline = false;
            if (this) Repaint();
        }
    }

    void DrawBuildTools(List<DlcRelease> picked)
    {
        using (new EditorGUI.DisabledScope(picked.Count == 0))
            if (GUILayout.Button("Analyze Addressables build layout for the checked releases"))
            {
                string path = EditorUtility.OpenFilePanel("Build layout JSON from this build", "Library/com.unity.addressables", "json");
                if (!string.IsNullOrEmpty(path))
                    report = string.Join("\n\n", picked.Select(r => r.releaseId + ":\n" + DlcBuildLayoutReport.Analyze(path, DlcReleaseService.Snapshot(r))));
            }
        if (GUILayout.Button("Record built artifact sizes and checksums"))
        {
            string folder = EditorUtility.OpenFolderPanel("Select built output directory", "", "");
            if (!string.IsNullOrEmpty(folder))
            {
                var inventory = DlcArtifactInventory.Capture(folder);
                inventory["releaseIds"] = new JArray(picked.Select(r => r.releaseId));
                string destination = EditorUtility.SaveFilePanel("Save inventory outside the output directory", "",
                    $"{DlcContentPublisher.CurrentPlatform}-{DlcContentPublisher.CurrentAppVersion}.artifacts.json", "json");
                if (!string.IsNullOrEmpty(destination))
                {
                    if (Path.GetFullPath(destination).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        report = "Save the inventory outside the directory it inventories.";
                    else
                    {
                        DlcReleaseService.WriteNew(destination, inventory.ToString());
                        report = "Recorded " + ((JArray)inventory["files"]).Count + " built files, " + EditorUtility.FormatBytes((long)inventory["totalBytes"]) + ".";
                    }
                }
            }
        }
        if (GUILayout.Button("Verify built artifacts against inventory"))
        {
            string path = EditorUtility.OpenFilePanel("Artifact inventory", "", "json");
            if (!string.IsNullOrEmpty(path))
            {
                string folder = EditorUtility.OpenFolderPanel("Directory to verify", "", "");
                if (!string.IsNullOrEmpty(folder)) report = DlcArtifactInventory.Verify(JObject.Parse(File.ReadAllText(path)), folder);
            }
        }
        if (GUILayout.Button("Open shared build / update / Azure upload tools"))
            if (!EditorApplication.ExecuteMenuItem("Build Utility/Seamless Build"))
                report = "Shared build utility menu is unavailable. Check com.arbelos.buildutility installation.";
    }

    void DrawExport()
    {
        EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);
        var picked = Checked();
        using (new EditorGUI.DisabledScope(!settings || picked.Count == 0))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button($"Validate {picked.Count} checked (dry run)")) RunExport(picked, true);
            if (GUILayout.Button($"Export {picked.Count} checked")) RunExport(picked, false);
        }
    }

    void RunExport(List<DlcRelease> picked, bool dryRun)
    {
        try
        {
            AssetDatabase.SaveAssets();
            var result = DlcContentExport.Export(picked, settings, dryRun);
            report = (result.Ok ? (dryRun ? "VALIDATION PASSED (nothing written)\n" : "EXPORTED\n") : $"{result.errors.Count} ERROR(S), nothing written\n") + result.Report();
            if (result.Ok) Debug.Log(report); else Debug.LogError(report);
            RefreshExports();
            exportIndex = 0;
        }
        catch (Exception e) { report = "Export failed: " + e; Debug.LogException(e); }
    }

    void DrawPublish()
    {
        EditorGUILayout.LabelField("Publish to staging", EditorStyles.boldLabel);
        if (!settings) return;
        if (exports.Count == 0) { EditorGUILayout.HelpBox($"No exports for {DlcContentPublisher.CurrentPlatform} {DlcContentPublisher.CurrentAppVersion} yet.", MessageType.None); }
        else
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                exportIndex = EditorGUILayout.Popup("Export", exportIndex, exports.Select(ExportLabel).ToArray());
                if (GUILayout.Button("Show", GUILayout.Width(50))) EditorUtility.RevealInFinder(Path.Combine(exports[exportIndex], "export.json"));
            }
            onlyCheckedProducts = EditorGUILayout.ToggleLeft("Only products of the checked releases", onlyCheckedProducts);
            allowNewCatalog = EditorGUILayout.ToggleLeft(new GUIContent(
                $"{DlcContentPublisher.CurrentAppVersion} hasn't shipped yet: allow a new catalog name",
                "Publishing normally stops if this app-version folder already has a catalog with a different name, which " +
                "means a full rebuild of a version players already have. Installed players keep loading the old catalog. " +
                "Use a content update for shipped versions. Tick this only while the version is unreleased."), allowNewCatalog);
            if (allowNewCatalog)
                EditorGUILayout.HelpBox("Installed players of this version won't see content published with a new catalog name. Untick after this publish.", MessageType.Warning);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(exports.Count == 0))
            {
                if (GUILayout.Button("Publish dry run")) StartPublish(true);
                if (GUILayout.Button("Publish…")) StartPublish(false);
            }
            if (GUILayout.Button("Status", GUILayout.Width(70))) Execute("status", (line, progress, cancel) => DlcContentPublisher.Status(settings));
        }
        DrawResume();
    }

    void DrawPromote()
    {
        EditorGUILayout.LabelField("Promote to production", EditorStyles.boldLabel);
        if (!settings) return;
        EditorGUILayout.HelpBox(
            $"Copies what's published in {settings.container} for {DlcContentPublisher.CurrentPlatform} {DlcContentPublisher.CurrentAppVersion} " +
            $"to {settings.productionContainer}, byte for byte: bundles, catalog and product manifests first, products.json last, so " +
            "production players get exactly what you tested. Nothing is rebuilt or re-exported. With 'Only products of the checked " +
            "releases' ticked, only those products are listed in production's products.json (bundles and the catalog are copied whole). " +
            "If it stops, run it again: files already copied are skipped.", MessageType.None);
        promoteAllowNewCatalog = EditorGUILayout.ToggleLeft(new GUIContent(
            $"{DlcContentPublisher.CurrentAppVersion} hasn't shipped yet: allow a new catalog name in production",
            "Like the publish option: only for an app version players don't have yet."), promoteAllowNewCatalog);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Promote dry run")) StartPromote(true);
            if (GUILayout.Button("Promote to production…")) StartPromote(false);
            if (GUILayout.Button("Production status", GUILayout.Width(120)))
                Execute("production status", (line, progress, cancel) => DlcContentPublisher.Status(settings, production: true));
        }
    }

    void StartPromote(bool dryRun)
    {
        List<string> products = null;
        if (onlyCheckedProducts)
        {
            products = Checked().SelectMany(r => r.productIds ?? new List<string>()).Select(id => (id ?? "").Trim())
                .Where(id => id.Length > 0).Distinct().ToList();
            if (products.Count == 0) { report = "Check the releases to promote, or untick 'Only products of the checked releases' to promote every staged product."; return; }
        }
        string what = products == null ? "every product published to staging" : $"{products.Count} product(s): {string.Join(", ", products.Take(8))}{(products.Count > 8 ? ", …" : "")}";
        bool allowNew = promoteAllowNewCatalog;
        if (!dryRun && !EditorUtility.DisplayDialog("Promote DLC content to PRODUCTION",
                $"Copy {what}\n\nfrom\n{DlcContentPublisher.Destination(settings)}\n\nto PRODUCTION\n{DlcContentPublisher.ProductionDestination(settings)}\n\n" +
                "Players of this app version see it as soon as products.json is written. Did you test it with a staging build?" +
                (allowNew ? "\n\nA new catalog name is ALLOWED in production. Only do this if the version hasn't shipped." : ""),
                "Promote to production", "Cancel"))
            return;
        if (!dryRun) promoteAllowNewCatalog = false;
        Execute(dryRun ? "promote dry run" : "promote",
            (line, progress, cancel) => DlcContentPublisher.Promote(settings, products, dryRun, allowNew, line, progress, cancel));
    }

    void DrawResume()
    {
        if (lastPublish == null || !lastPublish.failed) return;
        bool exportGone = !File.Exists(Path.Combine(lastPublish.folder ?? "", "export.json"));
        EditorGUILayout.HelpBox(
            $"The last publish ({lastPublish.what}) didn't finish. Everything it uploaded stays in place, and players " +
            "don't see any of it until products.json is written at the very end. Resume runs the same publish again: " +
            "files already uploaded are skipped and manifests it already wrote are reused." +
            (exportGone ? "\n\nIts export folder no longer exists, so it can't be resumed." : ""),
            MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(exportGone))
                if (GUILayout.Button("Resume publish")) ResumePublish();
            if (GUILayout.Button("Dismiss", GUILayout.Width(70))) lastPublish = null;
        }
    }

    void ResumePublish()
    {
        var job = lastPublish;
        if (!EditorUtility.DisplayDialog("Resume DLC publish",
                $"Publish {job.what} again from\n{job.folder}\n\nto\n{DlcContentPublisher.Destination(settings)}?\n\n" +
                "Files already uploaded are skipped.", "Resume", "Cancel"))
            return;
        RunPublish(job.folder, job.hasProducts ? job.products : null, false, job.allowNewCatalog, job.what);
    }

    void DrawProgress()
    {
        if (!busy) return;
        using (new EditorGUILayout.HorizontalScope())
        {
            Rect bar = EditorGUILayout.GetControlRect(GUILayout.Height(20));
            var p = shownProgress;
            EditorGUI.ProgressBar(bar, p?.Fraction ?? 0f, p != null ? p.Describe() : $"Running {runningLabel}…");
            using (new EditorGUI.DisabledScope(cancelSource == null || cancelSource.IsCancellationRequested))
                if (cancelSource != null && GUILayout.Button("Cancel", GUILayout.Width(70), GUILayout.Height(20)))
                    cancelSource.Cancel();
        }
        if (shownProgress != null && !string.IsNullOrEmpty(shownProgress.Item))
            EditorGUILayout.LabelField(shownProgress.Item, EditorStyles.miniLabel);
    }

    static string ExportLabel(string folder)
    {
        try
        {
            var export = JObject.Parse(File.ReadAllText(Path.Combine(folder, "export.json")));
            return $"{Path.GetFileName(folder)}  ({((JArray)export["products"]).Count} products)";
        }
        catch (Exception) { return Path.GetFileName(folder) + "  (unreadable)"; }
    }

    void StartPublish(bool dryRun)
    {
        string folder = exports[exportIndex];
        List<string> products = null;
        if (onlyCheckedProducts)
        {
            var inExport = new HashSet<string>(JObject.Parse(File.ReadAllText(Path.Combine(folder, "export.json")))["products"].Select(t => (string)t));
            products = Checked().SelectMany(r => r.productIds ?? new List<string>()).Select(id => (id ?? "").Trim())
                .Where(inExport.Contains).Distinct().ToList();
            if (products.Count == 0) { report = "None of the checked releases' products are in this export. Check the releases you exported, or untick 'Only products of the checked releases'."; return; }
        }
        string what = products == null ? "every product in the export" : $"{products.Count} product(s): {string.Join(", ", products.Take(8))}{(products.Count > 8 ? ", …" : "")}";
        bool allowNew = allowNewCatalog;
        if (!dryRun && !EditorUtility.DisplayDialog("Publish DLC content",
                $"Publish {what}\n\nfrom\n{folder}\n\nto\n{DlcContentPublisher.Destination(settings)}\n\nas the signed-in Azure CLI user?" +
                (allowNew ? "\n\nA new catalog name is ALLOWED for this app version. Only do this if it hasn't shipped." : ""), "Publish", "Cancel"))
            return;
        if (!dryRun) allowNewCatalog = false; // one publish at a time
        RunPublish(folder, products, dryRun, allowNew, what);
    }

    void RunPublish(string folder, List<string> products, bool dryRun, bool allowNew, string what)
    {
        if (!dryRun)
            lastPublish = new PublishJob { folder = folder, products = products ?? new List<string>(), hasProducts = products != null,
                allowNewCatalog = allowNew, what = what, failed = false };
        var job = lastPublish;
        Execute(dryRun ? "publish dry run" : "publish",
            (line, progress, cancel) => DlcContentPublisher.Publish(settings, folder, products, dryRun, allowNew, line, progress, cancel),
            code =>
            {
                if (!dryRun && job != null) job.failed = code != 0;
                if (!dryRun) RefreshRemote();
            });
    }

    void DrawRollback()
    {
        showRollback = EditorGUILayout.Foldout(showRollback, "Rollback", true);
        if (!showRollback || !settings) return;
        using (new EditorGUI.IndentLevelScope())
        {
            EditorGUILayout.HelpBox("Rewrites products.json for this platform and app version only. Version folders are never deleted. " +
                "Staging and production are rolled back separately; a later promote copies staging's entries over production's.", MessageType.None);
            rollbackProduction = EditorGUILayout.Popup("Container", rollbackProduction ? 1 : 0,
                new[] { "Staging (" + settings.container + ")", "Production (" + settings.productionContainer + ")" }) == 1;
            rollbackProduct = EditorGUILayout.TextField("Product ID", rollbackProduct);
            rollbackMode = EditorGUILayout.Popup("Action", rollbackMode, new[] { "Make this version current", "Drop this version from the list" });
            rollbackVersion = EditorGUILayout.TextField("Version", rollbackVersion);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(rollbackProduct) || string.IsNullOrWhiteSpace(rollbackVersion)))
            {
                if (GUILayout.Button("Rollback dry run")) StartRollback(true);
                if (GUILayout.Button("Rollback…")) StartRollback(false);
            }
        }
    }

    void StartRollback(bool dryRun)
    {
        string product = rollbackProduct.Trim(), version = rollbackVersion.Trim();
        bool drop = rollbackMode == 1, production = rollbackProduction;
        string where = production ? "PRODUCTION " + DlcContentPublisher.ProductionDestination(settings) : DlcContentPublisher.Destination(settings);
        if (!dryRun && !EditorUtility.DisplayDialog("Rollback DLC product",
                $"{(drop ? "Drop" : "Make current")} {version} of {product} in\n{where}products.json?", "Rollback", "Cancel"))
            return;
        Execute("rollback", (line, progress, cancel) => DlcContentPublisher.Rollback(settings, product, version, drop, dryRun, production));
    }

    /// <param name="action">Gets callbacks for output lines and progress (from background threads) and a cancel token.</param>
    /// <param name="finished">Called with the exit code (1 on an exception).</param>
    async void Execute(string label,
        Func<Action<string>, Action<DlcPublishProgress>, System.Threading.CancellationToken, System.Threading.Tasks.Task<(int code, string output)>> action,
        Action<int> finished = null)
    {
        busy = true;
        runningLabel = label;
        report = $"Running {label}…";
        lock (live) { liveTail.Clear(); liveProgress = null; liveDirty = false; }
        shownProgress = null;
        cancelSource = new System.Threading.CancellationTokenSource();
        Repaint();

        void OnLine(string l)
        {
            lock (live)
            {
                liveTail.Enqueue(l);
                while (liveTail.Count > LiveLines) liveTail.Dequeue();
                liveDirty = true;
            }
        }
        void OnProgress(DlcPublishProgress p)
        {
            lock (live) { liveProgress = p; liveDirty = true; }
        }

        int exit = 1;
        try
        {
            var (code, output) = await action(OnLine, OnProgress, cancelSource.Token);
            exit = code;
            report = (code == 0 ? "" : $"{label.ToUpperInvariant()} FAILED (exit {code})\n") + output;
            if (code == 0) Debug.Log("[DLC Publish]\n" + output); else Debug.LogError("[DLC Publish]\n" + output);
        }
        catch (Exception e) { report = $"{label} failed: {e}"; Debug.LogException(e); }
        finally
        {
            busy = false;
            cancelSource.Dispose();
            cancelSource = null;
            shownProgress = null;
            finished?.Invoke(exit);
            if (this) { reportScroll = Vector2.zero; Repaint(); }
        }
    }
}
