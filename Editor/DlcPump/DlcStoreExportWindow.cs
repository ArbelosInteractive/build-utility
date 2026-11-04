using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Build Utility > DLC Store Export: pick a DlcStoreExportSettings asset and a releases folder, seed the settings
/// from the checked releases' products, then validate and export the PlayFab / Apple / Google API payloads.
/// The export covers every product in the settings asset; checking releases only decides which products get added.
/// </summary>
public sealed class DlcStoreExportWindow : EditorWindow
{
    const string IconPath = "Packages/com.arbelos.buildutility/Editor/DlcPump/DlcPumpIcon.png";

    [SerializeField] DlcStoreExportSettings settings;
    [SerializeField] DlcReleaseListView list = new DlcReleaseListView();
    [SerializeField] bool showSettings = true;
    [SerializeField] string exportId = "";
    [SerializeField] string report = "";
    Vector2 scroll, reportScroll;

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
    }

    void OnProjectChange() { list.Refresh(); Repaint(); }

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
        using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
        {
            try
            {
                EditorGUILayout.LabelField("DLC Store Export", new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 }, GUILayout.Height(24));
                EditorGUILayout.HelpBox(
                    "Exports API payloads for every product in the store export settings: PlayFab Economy v2 drafts, Apple in-app purchases " +
                    "and Google Play one-time products. Check releases and use 'Add missing products' to put their products into the settings. " +
                    "Exporting needs no content build and sends no API requests. Upload the result with the IAP Uploader.", MessageType.Info);
                DrawSettings();
                EditorGUILayout.Space();
                string listMessage = list.Draw();
                if (listMessage != null) report = listMessage;
                EditorGUILayout.Space();
                DrawActions();
            }
            catch (Exception e) { report = e.Message; EditorGUILayout.HelpBox(e.Message, MessageType.Error); }
        }
        EditorGUILayout.Space();
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
                EditorPrefs.SetString(PrefKey("settings"), settings ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)) : "");
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

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Validate platform catalog export")) Validate();
            if (GUILayout.Button("Export platform catalogs (JSON)…")) Export();
        }
    }

    void Validate()
    {
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), settings.options, EffectiveExportId());
        var errors = DlcStoreCatalogExport.Validate(manifest, settings.options);
        report = errors.Count == 0
            ? $"PASS: required export inputs are complete for all {((JArray)manifest["products"]).Count} products. Apple response bindings are resolved at upload time."
            : $"{errors.Count} problem(s):\n" + string.Join("\n", errors);
    }

    void Export()
    {
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(DlcReleaseService.Catalog(), settings.options, EffectiveExportId());
        var errors = DlcStoreCatalogExport.Validate(manifest, settings.options);
        if (errors.Count > 0) { report = "Export blocked:\n" + string.Join("\n", errors); return; }
        string last = EditorPrefs.GetString(PrefKey("parent"), "");
        string parent = EditorUtility.OpenFolderPanel("Choose parent for a new store export folder", Directory.Exists(last) ? last : "", "");
        if (string.IsNullOrEmpty(parent)) return;
        EditorPrefs.SetString(PrefKey("parent"), parent);
        AssetDatabase.SaveAssets();
        string output = DlcStoreCatalogExport.Export(manifest, settings.options, parent);
        report = $"Exported all {((JArray)manifest["products"]).Count} configured products, ordered requests, validation report and instructions to {output}";
        EditorUtility.RevealInFinder(output);
    }
}
