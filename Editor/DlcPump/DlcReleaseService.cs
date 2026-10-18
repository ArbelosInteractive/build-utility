using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public static class DlcReleaseService
{
    public static string CatalogPath => DlcProject.Required.CatalogPath;
    public static JObject Catalog() => JObject.Parse(File.ReadAllText(CatalogPath));
    public static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    public static string HashFile(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public static List<string> Validate(DlcRelease release)
    {
        var issues = new List<string>();
        if (!release) { issues.Add("ERROR: Select or create a release."); return issues; }
        if (!Regex.IsMatch(release.releaseId ?? "", @"^[a-z0-9][a-z0-9._-]{0,79}$"))
            issues.Add("ERROR: Release ID must use lowercase letters, digits, dots, underscores or hyphens (maximum 80).");
        if (string.IsNullOrWhiteSpace(release.minimumClientVersion)) issues.Add("ERROR: Minimum client version is required.");
        if (string.IsNullOrWhiteSpace(release.notes)) issues.Add("WARNING: Release notes are empty.");
        var products = (JArray)Catalog()["products"];
        if (products == null) { issues.Add("ERROR: IAP catalog has no products."); return issues; }
        var ids = products.Select(p => (string)p["id"]).ToList();
        foreach (var duplicate in ids.GroupBy(id => id).Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
            issues.Add("ERROR: Empty or duplicate catalog ID: " + duplicate.Key);
        if (release.productIds.Count == 0) issues.Add("ERROR: Select at least one product.");
        foreach (var id in release.productIds)
        {
            if (string.IsNullOrWhiteSpace(id)) { issues.Add("ERROR: Empty selected product ID."); continue; }
            var product = products.FirstOrDefault(p => (string)p["id"] == id);
            if (product == null) { issues.Add("ERROR: Unknown product: " + id); continue; }
            if ((int?)product["type"] != 1) issues.Add("ERROR: DLC product must be non-consumable: " + id);
            if (string.IsNullOrWhiteSpace((string)product["defaultDescription"]?["title"])) issues.Add("ERROR: Missing product title: " + id);
            if (!(product["payouts"] is JArray payouts) || payouts.Count == 0) issues.Add("ERROR: Missing payout definition: " + id);
            if (!release.content.Any(c => c != null && c.productId == id && c.asset)) issues.Add("ERROR: Assign content for " + id);
            var policy = new List<string>();
            var expected = DlcProject.Required.Resolve(id, policy);
            issues.AddRange(policy.Where(s => s.StartsWith("ERROR:")));
            foreach (var required in expected)
                if (!release.content.Any(c => c != null && c.productId == id && c.asset == required.asset))
                    issues.Add("ERROR: Runtime product content is absent from the release: " + id + " / " + AssetDatabase.GetAssetPath(required.asset));
        }
        if (release.productIds.Distinct().Count() != release.productIds.Count) issues.Add("ERROR: Duplicate release product IDs.");
        var contentIdentities = release.content.Where(c => c != null && c.asset).Select(c =>
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c.asset, out string guid, out long localId);
            return c.productId + ":" + guid + ":" + localId;
        });
        if (contentIdentities.Distinct().Count() != contentIdentities.Count()) issues.Add("ERROR: Duplicate content assignment within a product.");
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (!settings) issues.Add("ERROR: Addressables settings are missing.");
        else if (string.IsNullOrEmpty(settings.profileSettings.GetProfileId(release.addressablesProfile)))
            issues.Add("ERROR: Addressables profile does not exist: " + release.addressablesProfile);
        if (release.target != EditorUserBuildSettings.activeBuildTarget) issues.Add("WARNING: Switch Unity's active build target to " + release.target + " before building.");
        var entries = settings ? settings.groups.Where(g => g).SelectMany(g => g.entries).ToList() : new List<AddressableAssetEntry>();
        foreach (var item in release.content)
        {
            if (item == null) { issues.Add("ERROR: Empty content record."); continue; }
            if (!release.productIds.Contains(item.productId)) issues.Add("ERROR: Content refers to an unselected product: " + item.productId);
            string path = AssetDatabase.GetAssetPath(item.asset);
            if (!item.asset || string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) { issues.Add("ERROR: Missing or invalid content asset for " + item.productId); continue; }
            if (item.asset is MonoScript || item.asset is SceneAsset) issues.Add("ERROR: Assign a content asset, not a script or scene: " + path);
            if (!string.IsNullOrWhiteSpace(item.addressablesKey))
            {
                var matches = entries.Where(e => e.address == item.addressablesKey).ToList();
                if (matches.Count != 1) issues.Add("ERROR: Addressables key must resolve uniquely: " + item.addressablesKey);
                else if (matches[0].guid != AssetDatabase.AssetPathToGUID(path)) issues.Add("ERROR: Addressables key points to another asset: " + item.addressablesKey);
            }
            else issues.Add("WARNING: No direct download key for " + path + "; verify it is included through a downloaded dependency or the client.");
        }
        foreach (var group in release.content.Where(c => c != null && c.asset).GroupBy(c => AssetDatabase.GetAssetPath(c.asset)).Where(g => g.Select(c => c.productId).Distinct().Count() > 1))
            issues.Add("INFO: Shared source asset across products (not necessarily duplicated bundles): " + group.Key);
        issues.Add("INFO: Store registration, entitlement delivery and remote downloads require separate staging verification.");
        return issues;
    }

    public static JObject Snapshot(DlcRelease release)
    {
        var errors = Validate(release).Where(s => s.StartsWith("ERROR:")).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException(string.Join("\n", errors));
        var products = (JArray)Catalog()["products"];
        var content = new JArray();
        var dependencyCache = new Dictionary<string, JObject>(StringComparer.Ordinal);
        foreach (var item in release.content.OrderBy(c => c.productId).ThenBy(c => AssetDatabase.GetAssetPath(c.asset)))
        {
            var path = AssetDatabase.GetAssetPath(item.asset);
            var dependencies = new JArray();
            foreach (var dep in AssetDatabase.GetDependencies(path, true).OrderBy(p => p))
            {
                if (!File.Exists(dep)) continue;
                if (!dependencyCache.TryGetValue(dep, out var record))
                {
                    record = new JObject { ["path"] = dep, ["sha256"] = HashFile(dep),
                        ["metaSha256"] = File.Exists(dep + ".meta") ? HashFile(dep + ".meta") : "",
                        ["sourceBytes"] = new FileInfo(dep).Length };
                    dependencyCache.Add(dep, record);
                }
                dependencies.Add(record.DeepClone());
            }
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(item.asset, out string guid, out long localId);
            content.Add(new JObject { ["productId"] = item.productId, ["path"] = path, ["guid"] = guid,
                ["localFileId"] = localId, ["addressablesKey"] = item.addressablesKey ?? "", ["dependencies"] = dependencies });
        }
        var configuration = new JArray();
        var settingsPath = AssetDatabase.GetAssetPath(AddressableAssetSettingsDefaultObject.Settings);
        foreach (var path in AssetDatabase.GetDependencies(settingsPath, true).Concat(new[] { "Packages/manifest.json", "Packages/packages-lock.json", "ProjectSettings/ProjectVersion.txt" }).Distinct().OrderBy(p => p))
            if (File.Exists(path)) configuration.Add(new JObject { ["path"] = path, ["sha256"] = HashFile(path) });
        return new JObject { ["schemaVersion"] = 1, ["releaseId"] = release.releaseId,
            ["minimumClientVersion"] = release.minimumClientVersion, ["target"] = release.target.ToString(),
            ["profile"] = release.addressablesProfile, ["unityVersion"] = Application.unityVersion,
            ["configuration"] = configuration, ["uniqueSourceBytes"] = dependencyCache.Values.Sum(d => (long)d["sourceBytes"]),
            ["sizeNote"] = "Source bytes only; not compressed download size.",
            ["notes"] = release.notes ?? "", ["products"] = new JArray(release.productIds.OrderBy(id => id)
                .Select(id => products.First(p => (string)p["id"] == id).DeepClone())), ["content"] = content };
    }

    public static void WriteNew(string path, string text)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
    }

    public static string Compare(JObject before, JObject after)
    {
        if ((int?)before["schemaVersion"] != 1 || (int?)after["schemaVersion"] != 1)
            throw new InvalidOperationException("Unsupported release manifest schema.");
        var lines = new List<string>();
        foreach (string section in new[] { "products", "content" })
        {
            string Key(JToken t) => section == "products" ? (string)t["id"] : (string)t["productId"] + ":" + (string)t["guid"] + ":" + (string)t["localFileId"];
            var oldItems = ((JArray)before[section]).ToDictionary(Key);
            var newItems = ((JArray)after[section]).ToDictionary(Key);
            foreach (var key in oldItems.Keys.Union(newItems.Keys).OrderBy(k => k))
                if (!oldItems.ContainsKey(key)) lines.Add("ADDED " + section + " " + key);
                else if (!newItems.ContainsKey(key)) lines.Add("REMOVED " + section + " " + key);
                else if (!JToken.DeepEquals(oldItems[key], newItems[key])) lines.Add("CHANGED " + section + " " + key);
        }
        foreach (string field in new[] { "minimumClientVersion", "target", "profile", "notes", "unityVersion", "configuration" })
            if (!JToken.DeepEquals(before[field], after[field])) lines.Add("CHANGED " + field);
        return lines.Count == 0 ? "No product, content or configuration changes." : string.Join("\n", lines);
    }
}
