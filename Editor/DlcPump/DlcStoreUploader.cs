using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

/// <summary>
/// Runs the IAP uploader (Tools~/IAP UPLOADER/upload_all.py) for the DLC Store Export window: lists the items in a
/// dlc-store-export folder, checks Key Vault access, and uploads chosen stores and items with live per-item status.
/// The Python scripts do the uploads; credentials are read from Key Vault as the signed-in Azure CLI user.
/// </summary>
public static class DlcStoreUploader
{
    public static readonly string[] Platforms = { "apple", "google", "playfab" };
    public static readonly Dictionary<string, string> Labels = new Dictionary<string, string> {
        ["apple"] = "Apple App Store", ["google"] = "Google Play", ["playfab"] = "PlayFab" };

    public const string Packages = "requests \"pyjwt[crypto]\" google-auth azure-identity azure-keyvault-secrets";

    public static string Script => DlcPythonProcess.PackageTool("IAP UPLOADER", "upload_all.py");
    public static string LogDir => Path.GetFullPath(Path.Combine("Logs", "iap-upload"));

    [Serializable]
    public sealed class Item
    {
        public string key, arg, id, name, details, problem;
        /// <summary>PlayFab only: "same" (uploaded to the staging title as it is now), "changed" or "no".</summary>
        public string staged;
        public bool selected = true;
        public string status = "";

        /// <summary>Whether a name printed in an upload summary refers to this item.</summary>
        public bool Matches(string printed)
        {
            if (string.IsNullOrEmpty(printed)) return false;
            foreach (string k in new[] { key, arg })
                if (!string.IsNullOrEmpty(k) && (printed == k || printed.StartsWith(k + " ", StringComparison.Ordinal) ||
                                                 printed.StartsWith(k + ":", StringComparison.Ordinal)))
                    return true;
            return false;
        }
    }

    /// <summary>dlc-store-export-* folders under <paramref name="parent"/>, newest first.</summary>
    public static List<string> Exports(string parent) =>
        !string.IsNullOrEmpty(parent) && Directory.Exists(parent)
            ? Directory.GetDirectories(parent, "dlc-store-export-*").Select(Path.GetFullPath)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList()
            : new List<string>();

    /// <summary>The items per platform, or an error.</summary>
    public static async Task<(Dictionary<string, List<Item>> items, string error)> List(string python, string export)
    {
        string json = null;
        var (code, output) = await DlcPythonProcess.Run(python, new[] { Script, "--export", export, "--list" },
            onMachine: (kind, data) => { if (kind == "items") json = data; }, idleTimeout: TimeSpan.FromMinutes(2));
        if (code != 0 || json == null) return (null, output.Trim().Length > 0 ? output.Trim() : $"upload_all.py --list failed (exit {code}).");
        try
        {
            var o = JObject.Parse(json);
            var items = Platforms.ToDictionary(p => p, p => (o[p] as JArray)?.Select(t => t.ToObject<Item>()).ToList() ?? new List<Item>());
            foreach (var item in items.Values.SelectMany(l => l)) { item.selected = item.problem == null; item.status = ""; }
            return (items, null);
        }
        catch (Exception e) { return (null, "Couldn't read the item list: " + e.Message); }
    }

    /// <summary>Vault access, plus the staging and production PlayFab title IDs it holds ("" when a secret is missing).</summary>
    public static async Task<(bool ok, string message, string stagingTitle, string productionTitle)> CheckVault(string python, string vault)
    {
        bool ok = false;
        string message = null, stagingTitle = "", productionTitle = "";
        var (code, output) = await DlcPythonProcess.Run(python, new[] { Script, "--vault", vault ?? "", "--check-vault" },
            onMachine: (kind, data) =>
            {
                if (kind != "vault") return;
                var o = JObject.Parse(data);
                ok = (bool?)o["ok"] ?? false;
                message = (string)o["message"];
                stagingTitle = (string)o["stagingTitleId"] ?? "";
                productionTitle = (string)o["productionTitleId"] ?? "";
            }, idleTimeout: TimeSpan.FromMinutes(2));
        return (ok, message ?? (output.Trim().Length > 0 ? output.Trim().Split('\n').Last() : $"check failed (exit {code})"),
            stagingTitle, productionTitle);
    }

    /// <param name="items">Per platform: null uploads every item of that platform, otherwise just these args.</param>
    /// <param name="onPlatform">(platform, state) as each store starts and ends; background thread.</param>
    /// <param name="onItem">(platform, printed name, status) as items finish; background thread.</param>
    /// <param name="playFabProduction">Upload PlayFab items to the production title; otherwise to the staging title.
    /// Production only accepts items whose exact file went to staging first, unless <paramref name="skipStagingCheck"/>.</param>
    public static Task<(int code, string output)> Upload(string python, string export, string vault,
        IDictionary<string, List<string>> items, bool dryRun, bool activateGoogle, bool publishPlayFab,
        Action<string> onLine, Action<string, string> onPlatform, Action<string, string, string> onItem, CancellationToken cancel,
        bool playFabProduction = false, bool skipStagingCheck = false)
    {
        var platforms = Platforms.Where(items.ContainsKey).ToList();
        var args = new List<string> { Script, "--export", export, "--vault", vault ?? "", "--log-dir", LogDir, "--progress", "--only" };
        args.AddRange(platforms);
        foreach (string p in platforms)
            if (items[p] != null && items[p].Count > 0) { args.Add($"--{p}-item"); args.AddRange(items[p]); }
        if (dryRun) args.Add("--dry-run");
        if (activateGoogle && platforms.Contains("google")) args.Add("--activate");
        if (publishPlayFab && platforms.Contains("playfab")) args.Add("--publish");
        if (platforms.Contains("playfab"))
        {
            args.AddRange(new[] { "--playfab-target", playFabProduction ? "production" : "staging" });
            if (playFabProduction && skipStagingCheck) args.Add("--skip-staging-check");
        }

        return DlcPythonProcess.Run(python, args, onLine, (kind, json) =>
        {
            var o = JObject.Parse(json);
            if (kind == "platform") onPlatform?.Invoke((string)o["platform"], (string)o["state"]);
            else if (kind == "item") onItem?.Invoke((string)o["platform"], (string)o["name"], (string)o["status"]);
        }, cancel, TimeSpan.FromMinutes(15));
    }

    /// <summary>pip install of the packages the upload scripts need.</summary>
    public static Task<(int code, string output)> InstallPackages(string python, Action<string> onLine, CancellationToken cancel) =>
        DlcPythonProcess.Run(python, new[] { "-m", "pip", "install", "requests", "pyjwt[crypto]", "google-auth", "azure-identity", "azure-keyvault-secrets" },
            onLine, null, cancel, TimeSpan.FromMinutes(10));
}
