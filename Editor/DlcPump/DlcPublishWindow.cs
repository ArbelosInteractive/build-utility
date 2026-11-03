using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build Utility > DLC Publish: pick publishing settings and a releases folder, check releases, export their
/// per-product manifests, then publish the export with Tools~/DlcPublisher/publish_dlc.py.
/// </summary>
public sealed class DlcPublishWindow : EditorWindow
{
    const string IconPath = "Packages/com.arbelos.buildutility/Editor/DlcPump/DlcPumpIcon.png";

    [SerializeField] DlcPublishingSettings settings;
    [SerializeField] DlcReleaseListView list = new DlcReleaseListView();
    [SerializeField] bool showSettings = true, showRollback, onlyCheckedProducts = true;
    [SerializeField] string report = "";
    [SerializeField] string rollbackProduct = "", rollbackVersion = "v1";
    [SerializeField] int rollbackMode;  // 0 = make current, 1 = drop
    [SerializeField] int exportIndex;

    List<string> exports = new List<string>();
    Vector2 scroll, reportScroll;
    bool busy;
    bool azureBusy, azureOk;
    bool allowNewCatalog; // deliberately not serialized: off again after a reload
    string azureStatus = "Azure: not checked yet";

    static string PrefKey(string name) => "Arbelos.DlcPublish." + PlayerSettings.productGUID + "." + name;

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
    void OnFocus() => RefreshExports();

    void RefreshExports()
    {
        exports = settings ? DlcContentPublisher.Exports(settings) : new List<string>();
        exportIndex = Mathf.Clamp(exportIndex, 0, Math.Max(0, exports.Count - 1));
    }

    List<DlcRelease> Checked() => list.Checked;

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(busy || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            DrawHeader();
            DrawSettings();
            EditorGUILayout.Space();
            string listMessage = list.Draw(true);
            if (listMessage != null) report = listMessage;
            EditorGUILayout.Space();
            DrawExport();
            EditorGUILayout.Space();
            DrawPublish();
            EditorGUILayout.Space();
            DrawRollback();
        }
        EditorGUILayout.Space();
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
            "1. Build Addressables for the release's profile.  2. Check releases and Export.  3. Publish dry run, then Publish.\n" +
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
        EditorGUILayout.LabelField("Destination", DlcContentPublisher.Destination(settings), EditorStyles.wordWrappedLabel);
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
        EditorGUILayout.LabelField("Publish", EditorStyles.boldLabel);
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
            if (GUILayout.Button("Status", GUILayout.Width(70))) Execute("status", () => DlcContentPublisher.Status(settings));
        }
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
        Execute(dryRun ? "publish dry run" : "publish", () => DlcContentPublisher.Publish(settings, folder, products, dryRun, allowNew));
    }

    void DrawRollback()
    {
        showRollback = EditorGUILayout.Foldout(showRollback, "Rollback", true);
        if (!showRollback || !settings) return;
        using (new EditorGUI.IndentLevelScope())
        {
            EditorGUILayout.HelpBox("Rewrites products.json for this platform and app version only. Version folders are never deleted.", MessageType.None);
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
        bool drop = rollbackMode == 1;
        if (!dryRun && !EditorUtility.DisplayDialog("Rollback DLC product",
                $"{(drop ? "Drop" : "Make current")} {version} of {product} in\n{DlcContentPublisher.Destination(settings)}products.json?", "Rollback", "Cancel"))
            return;
        Execute("rollback", () => DlcContentPublisher.Rollback(settings, product, version, drop, dryRun));
    }

    async void Execute(string label, Func<System.Threading.Tasks.Task<(int code, string output)>> action)
    {
        busy = true;
        report = $"Running {label}…";
        Repaint();
        try
        {
            var (code, output) = await action();
            report = (code == 0 ? "" : $"{label.ToUpperInvariant()} FAILED (exit {code})\n") + output;
            if (code == 0) Debug.Log("[DLC Publish]\n" + output); else Debug.LogError("[DLC Publish]\n" + output);
        }
        catch (Exception e) { report = $"{label} failed: {e}"; Debug.LogException(e); }
        finally
        {
            busy = false;
            if (this) { reportScroll = Vector2.zero; Repaint(); }
        }
    }
}
