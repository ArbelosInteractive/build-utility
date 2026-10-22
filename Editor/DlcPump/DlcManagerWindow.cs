using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public sealed class DlcManagerWindow : EditorWindow
{
    [SerializeField] DlcRelease release;
    [SerializeField] string search = "";
    [SerializeField] int tab;
    Vector2 scroll;
    string report = "Choose a release, then validate it.";
    bool building;
    string editingProduct, productTitle, productDescription, catalogHash;
    string previousContentState = "";
    string packId = "dlc_bundle_", packTitle = "", packDescription = "";
    string publishManifest = "", publishBuildReceipt = "", publishInventory = "", publishOutput = "";

    const string IconPath = "Packages/com.arbelos.buildutility/Editor/DlcPump/DlcPumpIcon.png";

    [MenuItem("Build Utility/DLC Pump")]

    public static void Open() => GetWindow<DlcManagerWindow>("DLC Pump");

    void OnEnable()
    {
        titleContent = new GUIContent("DLC Pump", AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath));
        minSize = new Vector2(620, 450);
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon) GUILayout.Label(icon, GUILayout.Width(64), GUILayout.Height(64));
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("DLC Pump", new GUIStyle(EditorStyles.boldLabel) { fontSize = 22 }, GUILayout.Height(28));
                EditorGUILayout.LabelField("ARBELOS INTERACTIVE  /  " + (DlcProject.Adapter?.DisplayName ?? "Configure project adapter"), EditorStyles.miniLabel);
            }
        }
        EditorGUILayout.HelpBox("Plan and validate local releases. Exporting or building does not publish products or grant ownership.", MessageType.Info);
        using (new EditorGUILayout.HorizontalScope())
        {
            release = (DlcRelease)EditorGUILayout.ObjectField("Release", release, typeof(DlcRelease), false);
            if (GUILayout.Button("New", GUILayout.Width(65))) CreateRelease();
        }
        tab = GUILayout.Toolbar(tab, new[] { "Release", "Products", "Content", "Validate", "Build", "Stores", "Publish" });
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(building || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
        {
            if (!release) EditorGUILayout.HelpBox("Create a release asset or select an existing one.", MessageType.Info);
            else
            {
                try
                {
                    switch (tab)
                    {
                        case 0: DrawRelease(); break;
                        case 1: DrawProducts(); break;
                        case 2: DrawContent(); break;
                        case 3: DrawValidation(); break;
                        case 4: DrawBuild(); break;
                        case 5: DrawStores(); break;
                        case 6: DrawPublishing(); break;
                    }
                }
                catch (Exception error) { report = error.Message; EditorGUILayout.HelpBox(report, MessageType.Error); }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    void CreateRelease()
    {
        string path = EditorUtility.SaveFilePanelInProject("Create DLC release", "DLC Release", "asset", "Save outside Resources; this is editor-only planning data.");
        if (string.IsNullOrEmpty(path)) return;
        if (path.Split('/').Contains("Resources")) { report = "Release assets must be outside Resources."; return; }
        var created = CreateInstance<DlcRelease>();
        created.target = EditorUserBuildSettings.activeBuildTarget;
        created.minimumClientVersion = PlayerSettings.bundleVersion;
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings) created.addressablesProfile = settings.profileSettings.GetProfileName(settings.activeProfileId);
        AssetDatabase.CreateAsset(created, path);
        Undo.RegisterCreatedObjectUndo(created, "Create DLC release");
        release = created;
        Selection.activeObject = created;
    }

    void DrawRelease()
    {
        var serialized = new SerializedObject(release);
        serialized.Update();
        foreach (string field in new[] { "releaseId", "minimumClientVersion", "target", "addressablesProfile", "notes", "publishingSettings" })
            EditorGUILayout.PropertyField(serialized.FindProperty(field), true);
        serialized.ApplyModifiedProperties();
        if (GUILayout.Button("Save release")) AssetDatabase.SaveAssets();
        if (GUILayout.Button("Open instructions"))
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DlcManagerWindow).Assembly);
            if (package == null) throw new InvalidOperationException("Cannot locate Build Utility package documentation.");
            EditorUtility.OpenWithDefaultApp(Path.Combine(package.resolvedPath, "Documentation~/DLC-PUMP.md"));
        }
        EditorGUILayout.HelpBox("Use a new release ID for each shipment. Exported manifests preserve the catalog and content snapshot for comparison.", MessageType.None);
    }

    void DrawProducts()
    {
        search = EditorGUILayout.TextField("Search", search);
        if (GUILayout.Button("Open existing IAP catalog")) AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(DlcReleaseService.CatalogPath));
        EditorGUILayout.HelpBox("Select existing non-consumable products. Product grants still follow the game's existing runtime rules.", MessageType.Info);
        foreach (var product in ((JArray)DlcReleaseService.Catalog()["products"]).Where(p => (int?)p["type"] == 1))
        {
            string id = (string)product["id"];
            string title = (string)product["defaultDescription"]?["title"] ?? id;
            if ((id + title).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool selected = release.productIds.Contains(id);
            bool next = EditorGUILayout.ToggleLeft(title + "  [" + id + "]", selected);
            if (next != selected)
            {
                Undo.RecordObject(release, "Change release product");
                if (next) release.productIds.Add(id); else release.productIds.Remove(id);
                EditorUtility.SetDirty(release);
            }
            if (next && GUILayout.Button("Edit listing: " + title, EditorStyles.miniButton))
            {
                editingProduct = id;
                productTitle = title;
                productDescription = (string)product["defaultDescription"]?["description"] ?? "";
                catalogHash = DlcReleaseService.Hash(File.ReadAllBytes(DlcReleaseService.CatalogPath));
            }
        }
        if (!string.IsNullOrEmpty(editingProduct))
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Edit " + editingProduct, EditorStyles.boldLabel);
            productTitle = EditorGUILayout.TextField("Title", productTitle);
            productDescription = EditorGUILayout.TextArea(productDescription, GUILayout.MinHeight(65));
            EditorGUILayout.HelpBox("Edits local marketing text only. Product IDs, prices, store overrides and payouts remain intact. A backup is saved automatically; live stores are unchanged.", MessageType.Info);
            if (GUILayout.Button("Save listing with backup"))
            {
                report = "Saved. Backup: " + DlcCatalogEditing.Save(catalogHash,
                    DlcCatalogEditing.Patch(DlcReleaseService.Catalog(), editingProduct, productTitle, productDescription));
                catalogHash = DlcReleaseService.Hash(File.ReadAllBytes(DlcReleaseService.CatalogPath));
            }
        }
        if (DlcProject.Adapter is IDlcPackAuthoring authoring)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Create a pack from selected products", EditorStyles.boldLabel);
            packId = EditorGUILayout.TextField("New product ID", packId);
            packTitle = EditorGUILayout.TextField("Title", packTitle);
            packDescription = EditorGUILayout.TextField("Description", packDescription);
            EditorGUILayout.HelpBox("Creates local catalog and runtime grant data through the project adapter. Register and price the new product in stores/backend before release. Existing product ownership is preserved.", MessageType.Info);
            if (GUILayout.Button("Create pack"))
            {
                string id = authoring.CreatePack(packId, packTitle, packDescription, release.productIds.ToArray());
                Undo.RecordObject(release, "Select new DLC pack");
                release.productIds.Clear(); release.productIds.Add(id); release.content.Clear();
                EditorUtility.SetDirty(release);
                report = DlcProject.Required.Populate(release);
            }
        }
    }

    void DrawContent()
    {
        if (GUILayout.Button("Discover content from project rules")) report = DlcProject.Required.Populate(release);
        EditorGUILayout.HelpBox("Assign content to selected product IDs. Include the actual downloadable prefab or asset and its existing Addressables key. References alone do not create runtime pack grants.", MessageType.Info);
        var serialized = new SerializedObject(release);
        serialized.Update();
        EditorGUILayout.PropertyField(serialized.FindProperty("content"), true);
        serialized.ApplyModifiedProperties();
        if (GUILayout.Button("Open unlockable catalog")) Selection.activeObject = DlcProject.Required.UnlockableCatalog;
        if (GUILayout.Button("Open Addressables settings")) Selection.activeObject = AddressableAssetSettingsDefaultObject.Settings;
        EditorGUILayout.TextArea(report, GUILayout.MinHeight(100));
    }

    void DrawValidation()
    {
        if (GUILayout.Button("Validate release")) report = string.Join("\n", DlcReleaseService.Validate(release));
        if (GUILayout.Button("Audit runtime product rules"))
        {
            var messages = new System.Collections.Generic.List<string>();
            foreach (var id in release.productIds) DlcProject.Required.Resolve(id, messages);
            report = string.Join("\n", messages);
        }
        if (GUILayout.Button("Calculate source dependency footprint"))
        {
            var snapshot = DlcReleaseService.Snapshot(release);
            report = "Unique source dependencies: " + EditorUtility.FormatBytes((long)snapshot["uniqueSourceBytes"]) +
                "\nThis is a source-file footprint, not the compressed player download size. Build-layout analysis is required for download budgets.";
        }
        if (GUILayout.Button("Export immutable manifest"))
        {
            var snapshot = DlcReleaseService.Snapshot(release);
            string path = EditorUtility.SaveFilePanel("Export release manifest (new file)", "", release.releaseId + ".json", "json");
            if (!string.IsNullOrEmpty(path))
            {
                DlcReleaseService.WriteNew(path, snapshot.ToString());
                report = "Exported " + path + "\nLocal snapshot only; not published.";
            }
        }
        if (GUILayout.Button("Compare with previous manifest"))
        {
            string path = EditorUtility.OpenFilePanel("Previous release manifest", "", "json");
            if (!string.IsNullOrEmpty(path)) report = DlcReleaseService.Compare(JObject.Parse(File.ReadAllText(path)), DlcReleaseService.Snapshot(release));
        }
        EditorGUILayout.TextArea(report, GUILayout.MinHeight(220));
    }

    void DrawStores()
    {
        DrawStoreCatalogExport();
        EditorGUILayout.HelpBox("Export requirements for PlayFab and the platform stores. Compare with a normalized snapshot exported from the intended environment. This does not publish listings or certify live purchase/restore tests.", MessageType.Info);
        if (GUILayout.Button("Export store-registration requirements"))
        {
            var requirements = DlcStoreReadiness.Requirements(DlcReleaseService.Snapshot(release));
            string path = EditorUtility.SaveFilePanel("Store requirements", "", release.releaseId + "-store-requirements.json", "json");
            if (!string.IsNullOrEmpty(path)) { DlcReleaseService.WriteNew(path, requirements.ToString()); report = "Exported store requirements: " + path; }
        }
        if (GUILayout.Button("Compare supplied store snapshot"))
        {
            string path = EditorUtility.OpenFilePanel("Normalized store snapshot", "", "json");
            if (!string.IsNullOrEmpty(path)) report = DlcStoreReadiness.Compare(
                DlcStoreReadiness.Requirements(DlcReleaseService.Snapshot(release)), JObject.Parse(File.ReadAllText(path)));
        }
        EditorGUILayout.TextArea(report, GUILayout.MinHeight(220));
    }

    JObject SelectedStoreProducts()
    {
        var products = (JArray)DlcReleaseService.Catalog()["products"];
        if (products == null) throw new InvalidOperationException("IAP catalog has no products.");
        var selected = new JArray();
        foreach (string id in release.productIds)
        {
            var found = products.Where(p => (string)p["id"] == id).ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Catalog product must resolve uniquely: " + id);
            selected.Add(found[0].DeepClone());
        }
        return new JObject { ["releaseId"] = release.releaseId, ["products"] = selected };
    }

    void DrawStoreCatalogExport()
    {
        EditorGUILayout.LabelField("Platform API catalog exports", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Export JSON for every product listed in these export settings, independently of the release's product selection. Required inputs must be complete for all configured products. Apple response IDs are resolved manually in the documented request sequence. Exporting requires no content build and sends no API requests.", MessageType.Info);
        var serialized = new SerializedObject(release);
        serialized.Update();
        EditorGUILayout.PropertyField(serialized.FindProperty("storeExportSettings"));
        serialized.ApplyModifiedProperties();
        if (!release.storeExportSettings)
        {
            if (GUILayout.Button("Create store export settings"))
            {
                string path = EditorUtility.SaveFilePanelInProject("Store export settings", "DLC Store Export Settings", "asset", "Save in an Editor folder.");
                if (!string.IsNullOrEmpty(path))
                {
                    if (path.Split('/').Contains("Resources")) throw new InvalidOperationException("Store export settings must be outside Resources.");
                    var settings = CreateInstance<DlcStoreExportSettings>();
                    AssetDatabase.CreateAsset(settings, path);
                    Undo.RecordObject(release, "Assign store export settings");
                    release.storeExportSettings = settings;
                    EditorUtility.SetDirty(release);
                }
            }
            return;
        }
        var settingsObject = new SerializedObject(release.storeExportSettings);
        settingsObject.Update();
        EditorGUILayout.PropertyField(settingsObject.FindProperty("options"), true);
        settingsObject.ApplyModifiedProperties();
        EditorGUILayout.LabelField("Export scope", "All " + (release.storeExportSettings.options?.products?.Count ?? 0) + " products in export settings");
        if (GUILayout.Button("Add missing selected products to export settings"))
        {
            Undo.RecordObject(release.storeExportSettings, "Add store export products");
            DlcStoreCatalogExport.AddMissingProducts(SelectedStoreProducts(), release.storeExportSettings.options);
            EditorUtility.SetDirty(release.storeExportSettings);
            report = "Added missing selected products without replacing existing settings. Fill locales, prices, territories and application IDs.";
        }
        if (GUILayout.Button("Validate platform catalog export"))
        {
            var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), release.storeExportSettings.options, release.releaseId);
            var errors = DlcStoreCatalogExport.Validate(manifest, release.storeExportSettings.options);
            report = errors.Count == 0 ? "PASS: Required export inputs are complete. Apple response bindings are resolved during manual submission." : string.Join("\n", errors);
        }
        if (GUILayout.Button("Export platform catalogs (JSON)"))
        {
            var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), release.storeExportSettings.options, release.releaseId);
            var errors = DlcStoreCatalogExport.Validate(manifest, release.storeExportSettings.options);
            if (errors.Count > 0) { report = "Export blocked:\n" + string.Join("\n", errors); return; }
            string parent = EditorUtility.OpenFolderPanel("Choose parent for a new store export folder", "", "");
            if (!string.IsNullOrEmpty(parent))
            {
                AssetDatabase.SaveAssets();
                string output = DlcStoreCatalogExport.Export(manifest, release.storeExportSettings.options, parent);
                report = "Exported all " + ((JArray)manifest["products"]).Count + " configured products, ordered requests, validation report and instructions to " + output;
                EditorUtility.RevealInFinder(output);
            }
        }
        EditorGUILayout.Space();
    }

    void DrawBuild()
    {
        EditorGUILayout.HelpBox("Build provider: shared com.arbelos.buildutility package. Project product and unlock configuration remains in this project.", MessageType.Info);
        if (GUILayout.Button("Open shared build / update / Azure upload tools"))
            if (!EditorApplication.ExecuteMenuItem("Build Utility/Seamless Build"))
                report = "Shared build utility menu is unavailable. Check com.arbelos.buildutility installation.";
        EditorGUILayout.HelpBox("Builds include all configured Addressables groups, not only this release. For a shipped client, select its original content-state baseline and use UPDATE. The baseline is archived with the receipt; errors never trigger a full-build fallback. Unity can be busy during builds.", MessageType.Warning);
        using (new EditorGUILayout.HorizontalScope())
        {
            previousContentState = EditorGUILayout.TextField("Content-state baseline", previousContentState);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string chosen = EditorUtility.OpenFilePanel("Original shipped client's Addressables content state", "", "bin");
                if (!string.IsNullOrEmpty(chosen)) previousContentState = chosen;
            }
        }
        if (GUILayout.Button("Build content UPDATE + receipt")) QueueBuild(true);
        if (GUILayout.Button("Build full Addressables content + receipt"))
            QueueBuild(false);
        if (GUILayout.Button("Analyze Addressables build layout"))
        {
            string path = EditorUtility.OpenFilePanel("Build layout JSON from this release's build", "Library/com.unity.addressables", "json");
            if (!string.IsNullOrEmpty(path)) report = DlcBuildLayoutReport.Analyze(path, DlcReleaseService.Snapshot(release)).ToString();
        }
        if (GUILayout.Button("Record built artifact sizes and checksums"))
        {
            string folder = EditorUtility.OpenFolderPanel("Select built output directory", "", "");
            if (!string.IsNullOrEmpty(folder))
            {
                var inventory = DlcArtifactInventory.Capture(folder);
                inventory["releaseId"] = release.releaseId;
                string destination = EditorUtility.SaveFilePanel("Save inventory outside the output directory", "", release.releaseId + ".artifacts.json", "json");
                if (!string.IsNullOrEmpty(destination))
                {
                    if (Path.GetFullPath(destination).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Save the inventory outside the directory it inventories.");
                    DlcReleaseService.WriteNew(destination, inventory.ToString());
                    report = "Recorded " + ((JArray)inventory["files"]).Count + " built files, " + EditorUtility.FormatBytes((long)inventory["totalBytes"]) + ".";
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
        EditorGUILayout.TextArea(report, GUILayout.MinHeight(150));
    }

    void DrawPublishing()
    {
        var serialized = new SerializedObject(release);
        serialized.Update();
        EditorGUILayout.PropertyField(serialized.FindProperty("publishingSettings"));
        serialized.ApplyModifiedProperties();
        if (!release.publishingSettings)
        {
            if (GUILayout.Button("Create publishing settings"))
            {
                string path = EditorUtility.SaveFilePanelInProject("Publishing settings", "DLC Publishing Settings", "asset", "Save in an Editor folder.");
                if (!string.IsNullOrEmpty(path))
                {
                    if (path.Split('/').Contains("Resources")) throw new InvalidOperationException("Publishing settings must be outside Resources.");
                    var settings = CreateInstance<DlcPublishingSettings>();
                    AssetDatabase.CreateAsset(settings, path);
                    Undo.RecordObject(release, "Assign publishing settings");
                    release.publishingSettings = settings; EditorUtility.SetDirty(release);
                }
            }
            return;
        }
        var config = new SerializedObject(release.publishingSettings);
        config.Update();
        var property = config.GetIterator();
        bool children = true;
        while (property.NextVisible(children)) { children = false; if (property.name != "m_Script") EditorGUILayout.PropertyField(property, true); }
        config.ApplyModifiedProperties();
        EditorGUILayout.HelpBox("Uploads to the configured container under prefix/release ID, verifies every remote file by downloading and hashing it, and writes a receipt. Existing files are never overwritten. Use a new release ID for changed artifacts. Addressables remote load paths must match this destination. PlayFab fields identify your environment; store purchases are not published by this action.", MessageType.Info);
        PublishFile("Manifest", ref publishManifest);
        PublishFile("Build receipt", ref publishBuildReceipt);
        PublishFile("Artifact inventory", ref publishInventory);
        using (new EditorGUILayout.HorizontalScope())
        {
            publishOutput = EditorGUILayout.TextField("Built output directory", publishOutput);
            if (GUILayout.Button("Browse", GUILayout.Width(65))) { string path = EditorUtility.OpenFolderPanel("Built output", "", ""); if (!string.IsNullOrEmpty(path)) publishOutput = path; }
        }
        if (GUILayout.Button("Upload and verify release files")) StartPublishing("publish");
        if (GUILayout.Button("Verify remote release files")) StartPublishing("verify");
        EditorGUILayout.TextArea(report, GUILayout.MinHeight(100));
    }

    static void PublishFile(string label, ref string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("Browse", GUILayout.Width(65))) { string path = EditorUtility.OpenFilePanel(label, "", "json"); if (!string.IsNullOrEmpty(path)) value = path; }
        }
    }

    async void StartPublishing(string action)
    {
        try
        {
            if ((string)JObject.Parse(File.ReadAllText(publishManifest))["releaseId"] != release.releaseId) throw new InvalidOperationException("Manifest does not belong to the selected release.");
            string receipt = EditorUtility.SaveFilePanel("New remote verification receipt", "", release.releaseId + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".remote.json", "json");
            if (string.IsNullOrEmpty(receipt)) return;
            building = true;
            report = "Uploading/verifying configured destination. Store publication remains separate.";
            report = await DlcPublisher.Run(release.publishingSettings, action, publishManifest, publishBuildReceipt, publishInventory, publishOutput, receipt);
        }
        catch (Exception error) { report = error.Message; }
        finally { building = false; if (this) Repaint(); }
    }

    void QueueBuild(bool update)
        {
            if (update && (string.IsNullOrWhiteSpace(previousContentState) || !File.Exists(previousContentState)))
                throw new InvalidOperationException("Select the original shipped client's content-state .bin file. An update never falls back to a full build.");
            AssetDatabase.SaveAssets();
            var snapshot = DlcReleaseService.Snapshot(release);
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (release.target != EditorUserBuildSettings.activeBuildTarget) throw new InvalidOperationException("Switch the active build target first.");
            if (settings.activeProfileId != settings.profileSettings.GetProfileId(release.addressablesProfile)) throw new InvalidOperationException("Select the release's Addressables profile before building.");
            string folder = EditorUtility.OpenFolderPanel("Choose build receipt directory", "", "");
            if (string.IsNullOrEmpty(folder)) return;
            string receipt = Path.Combine(folder, release.releaseId + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            DlcReleaseService.WriteNew(receipt + ".manifest.json", snapshot.ToString());
            string baseline = update ? Path.GetFullPath(previousContentState) : null;
            string baselineHash = update ? DlcReleaseService.Hash(File.ReadAllBytes(baseline)) : null;
            building = true;
            report = "Build queued. Output paths come from the active Addressables profile.";
            EditorApplication.delayCall += () => Build(receipt, snapshot, baseline, baselineHash);
    }

    void Build(string receipt, JObject snapshot, string baseline, string baselineHash)
    {
        var result = new JObject { ["releaseId"] = (string)snapshot["releaseId"],
            ["manifestSha256"] = DlcReleaseService.Hash(Encoding.UTF8.GetBytes(snapshot.ToString())),
            ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["published"] = false,
            ["buildKind"] = baseline == null ? "full" : "content-update", ["baselineSha256"] = baselineHash };
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Build cancelled: Editor state changed.");
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (!settings || EditorUserBuildSettings.activeBuildTarget.ToString() != (string)snapshot["target"] ||
                settings.profileSettings.GetProfileName(settings.activeProfileId) != (string)snapshot["profile"])
                throw new InvalidOperationException("Build cancelled: target or profile changed.");
            if (!release || !JToken.DeepEquals(snapshot, DlcReleaseService.Snapshot(release)))
                throw new InvalidOperationException("Build cancelled: release or content changed after the snapshot.");
            if (baseline != null)
            {
                if (!File.Exists(baseline) || DlcReleaseService.Hash(File.ReadAllBytes(baseline)) != baselineHash)
                    throw new InvalidOperationException("Content-state baseline changed since queueing.");
                // Copy the baseline into this immutable release receipt set. Do not
                // allow an output cleanup to erase the only shipped-client state.
                string archivedBaseline = receipt + ".baseline.bin";
                using (var input = File.OpenRead(baseline))
                using (var output = new FileStream(archivedBaseline, FileMode.CreateNew)) input.CopyTo(output);
                var updated = ContentUpdateScript.BuildContentUpdate(settings, archivedBaseline);
                if (updated == null || !string.IsNullOrEmpty(updated.Error))
                    throw new InvalidOperationException(updated?.Error ?? "No content-update result.");
                result["outputPath"] = updated.OutputPath;
                result["buildProvider"] = "Unity content update with explicit archived baseline";
            }
            else
            {
            // Reuse the shared package's checked build primitive. Older package
            // versions must be upgraded rather than silently bypassing it.
            var provider = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Arbelos.BuildUtility.Editor.CustomAddressableTools"))
                .FirstOrDefault(t => t != null);
            var build = provider?.GetMethod("BuildPlayerContentWithResult", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (build == null) throw new InvalidOperationException("The shared Arbelos build package needs BuildPlayerContentWithResult support. Update com.arbelos.buildutility.");
            try
            {
                var built = (AddressablesPlayerBuildResult)build.Invoke(null, null);
                result["outputPath"] = built.OutputPath;
            }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
            result["buildProvider"] = "com.arbelos.buildutility / BuildPlayerContentWithResult";
            }
            result["success"] = true;
            report = "Local Addressables build succeeded. Not uploaded or published. Receipt: " + receipt + ".build.json";
        }
        catch (Exception error) { result["success"] = false; result["error"] = error.ToString(); report = "Build failed: " + error.Message; }
        finally
        {
            result["finishedUtc"] = DateTime.UtcNow.ToString("O");
            try { DlcReleaseService.WriteNew(receipt + ".build.json", result.ToString()); }
            catch (Exception error) { Debug.LogException(error); report += "\nReceipt could not be saved: " + error.Message; }
            building = false;
            if (this) Repaint();
        }
    }
}
