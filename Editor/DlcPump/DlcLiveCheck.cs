using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arbelos.BuildUtility.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Runs Tools~/IAP UPLOADER/check_live.py: a read-only, per-product check of Google Play, App Store Connect, both
/// PlayFab titles, both DLC containers' products.json and the game data, for the current app version.
/// </summary>
public static class DlcLiveCheck
{
    public static readonly string[] Columns = { "google", "apple", "pf-staging", "pf-prod", "dlc-staging", "dlc-prod", "data-staging", "data-prod" };
    public static readonly string[] Headers = { "Google", "Apple", "PF stg", "PF prod", "DLC stg", "DLC prod", "Data stg", "Data prod" };

    public sealed class Cell { public string status = "", detail = ""; }

    public sealed class Row
    {
        public string id;
        public bool live;
        public Dictionary<string, Cell> cells = new Dictionary<string, Cell>();
        public bool Any(string status) => cells.Values.Any(c => c.status == status);
    }

    /// <summary>Where the inputs come from; an error names what is missing.</summary>
    public sealed class Inputs
    {
        public string python, vault, catalogPath, appleAppId, googlePackage, appVersion, stagingUrl, productionUrl;
        public string error;
    }

    public static Inputs Gather()
    {
        var inputs = new Inputs { appVersion = PlayerSettings.bundleVersion };
        var store = AssetDatabase.FindAssets("t:DlcStoreExportSettings").Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<DlcStoreExportSettings>).FirstOrDefault(s => s);
        if (!store) { inputs.error = "No DlcStoreExportSettings asset found (it holds the Key Vault, Apple app ID and Google package)."; return inputs; }
        inputs.python = store.pythonExecutable;
        inputs.vault = store.keyVaultName;
        inputs.appleAppId = store.options.appleAppId;
        inputs.googlePackage = store.options.googlePackageName;
        inputs.catalogPath = DlcProject.Adapter?.CatalogPath;
        var remote = Resources.Load<DlcRemoteConfig>("DlcRemoteConfig");
        inputs.stagingUrl = remote ? remote.stagingCdnBaseUrl : "";
        inputs.productionUrl = remote ? remote.cdnBaseUrl : "";
        if (string.IsNullOrEmpty(inputs.catalogPath) || !File.Exists(inputs.catalogPath)) inputs.error = "IAP catalog not found (project adapter CatalogPath).";
        else if (string.IsNullOrWhiteSpace(inputs.stagingUrl) || string.IsNullOrWhiteSpace(inputs.productionUrl))
            inputs.error = "Resources/DlcRemoteConfig needs both the staging and production URLs.";
        else if (string.IsNullOrWhiteSpace(inputs.vault)) inputs.error = $"Set the Key Vault name in {store.name}.";
        return inputs;
    }

    /// <param name="products">Product IDs to check; null or empty checks every product in the catalogs.</param>
    /// <param name="onRow">Each product's result as it arrives, on a background thread.</param>
    public static Task<(int code, string output)> Run(Inputs inputs, IEnumerable<string> products, Action<Row> onRow,
        Action<string> onLine = null, CancellationToken cancel = default)
    {
        var args = new List<string> { DlcPythonProcess.PackageTool("IAP UPLOADER", "check_live.py"), "--vault", inputs.vault,
            "--catalog", Path.GetFullPath(inputs.catalogPath), "--apple-app-id", inputs.appleAppId ?? "",
            "--google-package", inputs.googlePackage ?? "", "--app-version", inputs.appVersion,
            "--staging-url", inputs.stagingUrl.Trim(), "--production-url", inputs.productionUrl.Trim(), "--progress" };
        var only = products?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        if (only != null && only.Count > 0) { args.Add("--product"); args.AddRange(only); }
        return DlcPythonProcess.Run(inputs.python, args, onLine, (kind, json) =>
        {
            if (kind != "live") return;
            try
            {
                var o = JObject.Parse(json);
                var row = new Row { id = (string)o["id"], live = (bool?)o["live"] ?? false };
                foreach (var p in (JObject)o["cells"])
                    row.cells[p.Key] = new Cell { status = (string)p.Value["status"] ?? "", detail = (string)p.Value["detail"] ?? "" };
                onRow?.Invoke(row);
            }
            catch (Exception e) { Debug.LogWarning("[DLC live check] Unreadable result: " + e.Message); }
        }, cancel, TimeSpan.FromMinutes(5));
    }

    static GUIStyle cellStyle;

    /// <summary>Draws the results as a grid: one row per product, a colored cell per check (hover for the detail).</summary>
    public static void DrawTable(IReadOnlyList<Row> rows, float width)
    {
        cellStyle ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        float idWidth = Mathf.Clamp(width * 0.3f, 140, 260), cellWidth = Mathf.Max(48, (width - idWidth - 50) / Columns.Length);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Product", EditorStyles.miniBoldLabel, GUILayout.Width(idWidth));
            foreach (string h in Headers) GUILayout.Label(h, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(cellWidth));
            GUILayout.Label("Live", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(40));
        }
        foreach (var row in rows)
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent(row.id, row.id), EditorStyles.miniLabel, GUILayout.Width(idWidth));
                foreach (string col in Columns)
                {
                    row.cells.TryGetValue(col, out var c);
                    Rect r = GUILayoutUtility.GetRect(cellWidth, 16, GUILayout.Width(cellWidth));
                    EditorGUI.DrawRect(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), Color(c?.status));
                    GUI.Label(r, new GUIContent(c?.status ?? "?", c?.detail ?? ""), cellStyle);
                }
                Rect l = GUILayoutUtility.GetRect(40, 16, GUILayout.Width(40));
                GUI.Label(l, row.live ? "YES" : "no", cellStyle);
            }
    }

    static Color Color(string status) =>
        status == "OK" ? new Color(0.20f, 0.55f, 0.25f, 0.85f)
        : status == "WARN" ? new Color(0.75f, 0.55f, 0.10f, 0.85f)
        : status == "FAIL" ? new Color(0.70f, 0.20f, 0.20f, 0.85f)
        : new Color(0.4f, 0.4f, 0.4f, 0.6f);

    /// <summary>The non-OK cells as text, for the report.</summary>
    public static string Describe(IEnumerable<Row> rows) =>
        string.Join("\n", rows.Where(r => r.cells.Values.Any(c => c.status != "OK")).Select(r =>
            r.id + (r.live ? "  (live)" : "") + "\n" + string.Join("\n", Columns.Where(c => r.cells.TryGetValue(c, out var x) && x.status != "OK")
                .Select(c => $"    {c}: {r.cells[c].status} {r.cells[c].detail}"))));
}
