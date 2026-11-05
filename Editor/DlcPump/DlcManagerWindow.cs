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
    string editingProduct, productTitle, productDescription, catalogHash;
    string packId = "dlc_bundle_", packTitle = "", packDescription = "";

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
            if (GUILayout.Button("Release Waves")) DlcArtistWindow.Open();
            if (GUILayout.Button("Icon Studio")) DlcIconStudioWindow.Open();
            if (GUILayout.Button("DLC Publish")) DlcPublishWindow.Open();
            if (GUILayout.Button("Store Exports")) DlcStoreExportWindow.Open();
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            release = (DlcRelease)EditorGUILayout.ObjectField("Release", release, typeof(DlcRelease), false);
            if (GUILayout.Button("New", GUILayout.Width(65))) CreateRelease();
        }
        tab = GUILayout.Toolbar(Mathf.Clamp(tab, 0, 4), new[] { "Release", "Products", "Content", "Validate", "Stores" });
        scroll = EditorGUILayout.BeginScrollView(scroll);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
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
                        case 4: DrawStores(); break;
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
        foreach (string field in new[] { "releaseId", "minimumClientVersion", "addressablesProfile", "notes" })
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
            bool next;
            using (new EditorGUILayout.HorizontalScope())
            {
                next = EditorGUILayout.ToggleLeft(title + "  [" + id + "]", selected);
                if (selected) ProductIcon(id);
            }
            if (next != selected)
            {
                Undo.RecordObject(release, "Change release product");
                if (next) release.productIds.Add(id); else release.productIds.Remove(id);
                EditorUtility.SetDirty(release);
            }
            if (next)
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Edit listing: " + title, EditorStyles.miniButton))
                    {
                        editingProduct = id;
                        productTitle = title;
                        productDescription = (string)product["defaultDescription"]?["description"] ?? "";
                        catalogHash = DlcReleaseService.Hash(File.ReadAllBytes(DlcReleaseService.CatalogPath));
                    }
                    if (GUILayout.Button(new GUIContent("Edit icon", "Open this product's icon in Icon Studio (or start a new one)."), EditorStyles.miniButton, GUILayout.Width(80)))
                        DlcIconStudioWindow.ForProduct(id);
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

    /// <summary>The product's store icon (from the project adapter), 32×32, or a blank space when it has none yet.</summary>
    static void ProductIcon(string productId)
    {
        string path = (DlcProject.Adapter as IDlcIconAuthoring)?.IconAssetPath(productId);
        var texture = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        var rect = GUILayoutUtility.GetRect(32, 32, GUILayout.Width(32), GUILayout.Height(32));
        if (texture) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
        else GUI.Label(rect, new GUIContent("—", "No icon yet. Use Edit icon."), EditorStyles.centeredGreyMiniLabel);
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
}
