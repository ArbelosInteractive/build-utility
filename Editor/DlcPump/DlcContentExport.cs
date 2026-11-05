using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arbelos.BuildUtility.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets.ResourceLocators;

public sealed class DlcContentProductDraft
{
    public string productId, releaseId, minimumClientVersion, notes;
    public List<string> keys = new List<string>();
}

public sealed class DlcContentExportResult
{
    public string platform, appVersion, profileName, remoteLoadPath, buildPath, catalogFile, hashFile, catalogBuild;
    /// <summary>The full build's addressables_content_state.bin, when it belongs to <see cref="catalogFile"/>; publish stores it once per platform and app version.</summary>
    public string contentStateFile;
    public List<string> releaseIds = new List<string>();
    public List<DlcContentProductDraft> products = new List<DlcContentProductDraft>();
    public List<string> errors = new List<string>(), warnings = new List<string>();
    /// <summary>Products with no Addressables keys (local content only). They are still exported, with keys: [].</summary>
    public List<string> localOnlyProducts = new List<string>();
    /// <summary>Content entries with a blank addressablesKey (local content), not exported.</summary>
    public int ignoredEntries;
    public string outputDirectory;
    public bool Ok => errors.Count == 0;

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DLC content export for {platform} {appVersion} (profile '{profileName}')");
        sb.AppendLine($"Products: {products.Count}, keys: {products.Sum(p => p.keys.Count)}, catalog: {catalogFile} ({catalogBuild})");
        if (contentStateFile != null) sb.AppendLine("Content-state baseline: " + contentStateFile);
        if (ignoredEntries > 0) sb.AppendLine($"Ignored {ignoredEntries} content entr{(ignoredEntries == 1 ? "y" : "ies")} with a blank Addressables key (local content).");
        if (localOnlyProducts.Count > 0) sb.AppendLine($"{localOnlyProducts.Count} product(s) have no Addressables keys and are published with an empty key list (nothing to download): " + string.Join(", ", localOnlyProducts));
        foreach (var e in errors) sb.AppendLine("ERROR: " + e);
        foreach (var w in warnings) sb.AppendLine("WARNING: " + w);
        if (outputDirectory != null) sb.AppendLine("Written to " + outputDirectory);
        return sb.ToString();
    }
}

/// <summary>
/// Exports one product.json per product from DlcRelease assets into a local staging folder:
///   &lt;staging&gt;/&lt;Platform&gt;/&lt;appVersion&gt;/&lt;utc&gt;/export.json, validation.json, &lt;productId&gt;/product.json
/// Keys are the content entries' addressablesKey values (real Addressables addresses). Entries with a blank key are
/// local content and are ignored; a product with no keys is still exported (keys: []) so products.json records
/// every product that belongs to the app version. Each key is checked against the
/// Addressables settings and the built remote catalog. Version numbers are assigned at publish time by Tools~/DlcPublisher/publish_dlc.py.
/// </summary>
public static class DlcContentExport
{
    public static DlcContentExportResult Export(IList<DlcRelease> releases, DlcPublishingSettings publishing, bool dryRun)
    {
        var r = new DlcContentExportResult();
        releases = (releases ?? new List<DlcRelease>()).Where(x => x).ToList();
        if (releases.Count == 0) { r.errors.Add("Select at least one release."); return r; }
        if (!publishing) { r.errors.Add("Select a DlcPublishingSettings asset."); return r; }
        r.releaseIds = releases.Select(x => x.releaseId).ToList();

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (!settings) { r.errors.Add("No Addressables settings found in this project."); return r; }

        ResolveEnvironment(releases, publishing, settings, r);
        ResolveContentState(settings, r);
        var known = KnownAddresses(settings);
        HashSet<string> catalogKeys = null;
        if (r.catalogFile != null)
        {
            try { catalogKeys = ReadCatalogKeys(Path.Combine(r.buildPath, r.catalogFile)); }
            catch (Exception e) { r.errors.Add($"Couldn't read keys from catalog {r.catalogFile}: {e.Message}"); }
        }

        Validate(releases, known, catalogKeys, r.appVersion, r);
        if (!r.Ok || dryRun) return r;

        Write(publishing, r);
        return r;
    }

    /// <summary>
    /// The pure rule checks; there are no Addressables or file-system calls in here, so tests can run them.
    /// <paramref name="catalogKeys"/> may be null to skip the built-catalog check.
    /// </summary>
    public static void Validate(IList<DlcRelease> releases, ISet<string> knownAddresses, ISet<string> catalogKeys,
        string appVersion, DlcContentExportResult r)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            string rel = $"Release '{release.releaseId}' ({AssetDatabase.GetAssetPath(release)})";
            if (string.IsNullOrWhiteSpace(release.releaseId)) r.errors.Add(rel + ": releaseId is empty.");

            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in release.productIds ?? new List<string>())
            {
                string id = (raw ?? "").Trim();
                if (!DlcContentPaths.IsSafeSegment(id)) { r.errors.Add($"{rel}: product ID '{raw}' can only contain letters, digits, '.', '_' and '-'."); continue; }
                if (!listed.Add(id)) { r.errors.Add($"{rel}: product '{id}' is listed twice in productIds."); continue; }
                if (owner.TryGetValue(id, out string other)) { r.errors.Add($"Product '{id}' is in both release '{other}' and '{release.releaseId}'. Export it from one release only."); continue; }
                owner[id] = release.releaseId;
            }
            if (listed.Count == 0) r.errors.Add(rel + ": productIds is empty.");

            var keysByProduct = listed.ToDictionary(id => id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            var content = release.content ?? new List<DlcReleaseContent>();
            for (int i = 0; i < content.Count; i++)
            {
                var c = content[i];
                string where = $"{rel}, content #{i + 1}" + (c?.asset ? $" ({c.asset.name})" : "");
                if (c == null) { r.errors.Add(where + " is empty."); continue; }
                string pid = (c.productId ?? "").Trim();
                if (pid.Length == 0) { r.errors.Add(where + ": productId is empty."); continue; }
                if (!listed.Contains(pid)) { r.errors.Add($"{where}: productId '{pid}' isn't listed in this release's productIds."); continue; }

                string key = (c.addressablesKey ?? "").Trim();
                if (key.Length == 0) { r.ignoredEntries++; continue; } // local content, not downloaded
                if (!knownAddresses.Contains(key)) { r.errors.Add($"{where}: addressablesKey '{key}' isn't an address in the Addressables settings."); continue; }
                if (catalogKeys != null && !catalogKeys.Contains(key))
                    r.errors.Add($"{where}: key '{key}' isn't in the built remote catalog. Rebuild Addressables for this profile, then export again.");
                keysByProduct[pid].Add(key);
            }

            if (!string.IsNullOrWhiteSpace(release.minimumClientVersion) && !string.IsNullOrEmpty(appVersion)
                && !DlcVersion.Satisfies(appVersion, release.minimumClientVersion))
                r.warnings.Add($"{rel}: minimumClientVersion {release.minimumClientVersion} is newer than app version {appVersion}; clients of {appVersion} will skip these products.");

            foreach (var kv in keysByProduct.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Value.Count == 0) r.localOnlyProducts.Add(kv.Key);
                r.products.Add(new DlcContentProductDraft {
                    productId = kv.Key, releaseId = release.releaseId,
                    minimumClientVersion = release.minimumClientVersion ?? "", notes = release.notes ?? "",
                    keys = kv.Value.ToList() });
            }
        }
    }

    static void ResolveEnvironment(IList<DlcRelease> releases, DlcPublishingSettings publishing, AddressableAssetSettings settings, DlcContentExportResult r)
    {
        // Releases cover every store platform; the export is for the editor's active build target.
        var target = EditorUserBuildSettings.activeBuildTarget;
        r.platform = target.ToString();
        r.appVersion = PlayerSettings.bundleVersion;
        if (!DlcReleaseService.IsStorePlatform(target))
            r.errors.Add($"Active build target is {target}. DLC is exported for Android or iOS: switch platform and build Addressables first.");
        if (!DlcContentPaths.IsSafeSegment(r.appVersion)) r.errors.Add($"App version '{r.appVersion}' can't be used as a folder name.");

        var profiles = releases.Select(x => (x.addressablesProfile ?? "").Trim()).Distinct().ToList();
        if (profiles.Count > 1) { r.errors.Add("Selected releases use different Addressables profiles: " + string.Join(", ", profiles)); return; }
        r.profileName = profiles[0].Length > 0 ? profiles[0] : settings.profileSettings.GetProfileName(settings.activeProfileId);
        string profileId = settings.profileSettings.GetProfileId(r.profileName);
        if (string.IsNullOrEmpty(profileId)) { r.errors.Add($"Addressables profile '{r.profileName}' doesn't exist."); return; }
        if (!settings.BuildRemoteCatalog) r.errors.Add("Addressables 'Build Remote Catalog' is off. The DLC pipeline needs one remote catalog per app version.");

        string suffix = "/" + r.platform + "/" + r.appVersion;
        r.remoteLoadPath = (settings.RemoteCatalogLoadPath.GetValue(settings.profileSettings, profileId) ?? "").TrimEnd('/');
        string buildPath = (settings.RemoteCatalogBuildPath.GetValue(settings.profileSettings, profileId) ?? "").Replace('\\', '/').TrimEnd('/');
        if (!r.remoteLoadPath.EndsWith(suffix, StringComparison.Ordinal))
            r.errors.Add($"Remote.LoadPath of profile '{r.profileName}' is '{r.remoteLoadPath}'. It must end with '{suffix}': set it to <cdn>/[BuildTarget]/[UnityEngine.Application.version].");
        // The URL is read at runtime, so the same catalog works from the staging and the production container.
        if (r.remoteLoadPath != DlcEnvironment.LoadPathToken + suffix)
            r.warnings.Add($"Remote.LoadPath of profile '{r.profileName}' is the fixed URL '{r.remoteLoadPath}', not '{DlcEnvironment.RemoteLoadPath}'. " +
                           "This build loads from that URL whichever container it's published or promoted to. Fine for content updates of an app " +
                           "version that shipped with it; use the runtime content URL for new app versions.");
        CheckRemoteConfig(publishing, r.errors, r.warnings);
        if (!buildPath.EndsWith(suffix, StringComparison.Ordinal))
            r.errors.Add($"Remote.BuildPath of profile '{r.profileName}' is '{buildPath}'. It must end with '{suffix}', e.g. ServerData/[BuildTarget]/[UnityEngine.Application.version].");

        r.buildPath = Path.GetFullPath(buildPath);
        if (!Directory.Exists(r.buildPath)) { r.errors.Add($"Addressables build output '{r.buildPath}' doesn't exist. Build Addressables for profile '{r.profileName}' first."); return; }
        var catalogs = Directory.GetFiles(r.buildPath, "catalog*.*").Where(f => f.EndsWith(".bin") || f.EndsWith(".json"))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        if (catalogs.Count == 0) { r.errors.Add($"No remote catalog in '{r.buildPath}'. Build Addressables first."); return; }
        if (catalogs.Count > 1) r.warnings.Add($"Several catalogs in the build folder; using the newest, {Path.GetFileName(catalogs[0])}. Only that one is published.");
        string hash = Path.ChangeExtension(catalogs[0], ".hash");
        if (!File.Exists(hash)) { r.errors.Add($"Catalog hash file {Path.GetFileName(hash)} is missing."); return; }
        r.catalogFile = Path.GetFileName(catalogs[0]);
        r.hashFile = Path.GetFileName(hash);
        r.catalogBuild = File.ReadAllText(hash).Trim();
    }

    /// <summary>The DlcRemoteConfig in a Resources folder, the one the game loads; null when there is none.</summary>
    public static DlcRemoteConfig RemoteConfig() =>
        AssetDatabase.FindAssets("t:" + nameof(DlcRemoteConfig)).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.Replace('\\', '/').Contains("/Resources/") && Path.GetFileNameWithoutExtension(p) == "DlcRemoteConfig")
            .Select(AssetDatabase.LoadAssetAtPath<DlcRemoteConfig>).FirstOrDefault(c => c);

    /// <summary>Both runtime URLs set, and pointing at the publishing settings' containers.</summary>
    public static void CheckRemoteConfig(DlcPublishingSettings publishing, List<string> errors, List<string> warnings)
    {
        var remote = RemoteConfig();
        if (!remote) { errors.Add("No Resources/DlcRemoteConfig asset. Create one (Build Utility/DLC Remote Config) with the staging and production URLs."); return; }
        void Check(string url, string field, string container)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (url.Length == 0) errors.Add($"DlcRemoteConfig.{field} is empty.");
            else if (!string.IsNullOrWhiteSpace(container) && !url.EndsWith("/" + container.Trim(), StringComparison.Ordinal))
                warnings.Add($"DlcRemoteConfig.{field} is '{url}', which doesn't end with the container '{container}'. Check that the CDN serves that container there.");
        }
        Check(remote.stagingCdnBaseUrl, nameof(remote.stagingCdnBaseUrl), publishing.container);
        Check(remote.cdnBaseUrl, nameof(remote.cdnBaseUrl), publishing.productionContainer);
        if (string.Equals((remote.cdnBaseUrl ?? "").Trim().TrimEnd('/'), (remote.stagingCdnBaseUrl ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            errors.Add("DlcRemoteConfig has the same URL for staging and production. Store builds must read the production container.");
        if (string.Equals((publishing.container ?? "").Trim(), (publishing.productionContainer ?? "").Trim(), StringComparison.Ordinal))
            errors.Add($"{publishing.name}: the staging and production containers are the same.");
    }

    /// <summary>
    /// Picks the content-state file to store with this export. Only full builds write it, and its playerVersion names
    /// the catalog (catalog_&lt;playerVersion&gt;), so it is included only when it belongs to the catalog being exported.
    /// After a content update built from an older baseline, the local file is usually from a later full build; it is
    /// then left out and the baseline already stored in Azure stays.
    /// </summary>
    static void ResolveContentState(AddressableAssetSettings settings, DlcContentExportResult r)
    {
        if (r.catalogFile == null) return;
        string path;
        try { path = ContentUpdateScript.GetContentStateDataPath(false, settings); }
        catch (Exception e) { r.warnings.Add("Couldn't find the Addressables content-state file: " + e.Message); return; }
        if (string.IsNullOrEmpty(path) || path.Contains("://")) { r.warnings.Add($"Content State Build Path '{path}' isn't a local file; the content-state baseline won't be stored."); return; }
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            r.warnings.Add($"No content-state file at {path}; the baseline for {r.platform} {r.appVersion} won't be stored. It's written by full Addressables builds.");
            return;
        }
        var state = ContentUpdateScript.LoadContentState(path);
        string expected = Path.GetFileNameWithoutExtension(r.catalogFile);
        if (state == null || "catalog_" + state.playerVersion != expected)
        {
            r.warnings.Add($"{path} is from another full build (catalog_{state?.playerVersion}), not the one that made {r.catalogFile}, so it won't be stored. " +
                           "That's expected after a content update; for a full build, export straight after building.");
            return;
        }
        r.contentStateFile = path;
    }

    static HashSet<string> KnownAddresses(AddressableAssetSettings settings)
    {
        var entries = new List<AddressableAssetEntry>();
        settings.GetAllAssets(entries, false);
        return new HashSet<string>(entries.Select(e => e.address).Where(a => !string.IsNullOrEmpty(a)), StringComparer.Ordinal);
    }

    /// <summary>String keys in a built content catalog (binary by default in Addressables 2.x, JSON with ENABLE_JSON_CATALOG).</summary>
    public static HashSet<string> ReadCatalogKeys(string catalogPath)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
#if ENABLE_JSON_CATALOG
        var data = JsonUtility.FromJson<ContentCatalogData>(File.ReadAllText(catalogPath));
        foreach (var key in data.CreateLocator().Keys) if (key is string s) keys.Add(s);
#else
        // ExtractBinaryCatalog is the public reader for binary catalogs. Each unindented line starts with a key.
        string text = Path.Combine(Path.GetTempPath(), "dlc-catalog-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            ContentCatalogData.ExtractBinaryCatalog(catalogPath, text);
            foreach (string line in File.ReadLines(text))
            {
                if (line.Length == 0 || line[0] == '\t') continue;
                int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
                keys.Add(arrow >= 0 ? line.Substring(0, arrow) : line);
            }
        }
        finally { if (File.Exists(text)) File.Delete(text); }
#endif
        return keys;
    }

    static void Write(DlcPublishingSettings publishing, DlcContentExportResult r)
    {
        string root = DlcContentPublisher.StagingRoot(publishing);
        string assets = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
        if ((root + Path.DirectorySeparatorChar).StartsWith(assets, StringComparison.OrdinalIgnoreCase))
        { r.errors.Add("stagingDirectory must be outside Assets."); return; }

        string dir = Path.Combine(root, r.platform, r.appVersion, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
        if (Directory.Exists(dir)) { r.errors.Add($"{dir} already exists. Try again in a second."); return; }
        Directory.CreateDirectory(dir);

        foreach (var p in r.products)
        {
            Directory.CreateDirectory(Path.Combine(dir, p.productId));
            var product = new JObject {
                ["schemaVersion"] = DlcContentPaths.SchemaVersion, ["productId"] = p.productId,
                ["version"] = null, // assigned by publish_dlc.py
                ["platform"] = r.platform, ["appVersion"] = r.appVersion, ["releaseId"] = p.releaseId,
                ["minimumClientVersion"] = p.minimumClientVersion,
                ["keys"] = new JArray(p.keys), ["catalogBuild"] = r.catalogBuild, ["notes"] = p.notes };
            DlcReleaseService.WriteNew(Path.Combine(dir, p.productId, DlcContentPaths.ProductFile), product.ToString(Formatting.Indented));
        }

        var export = new JObject {
            ["schemaVersion"] = DlcContentPaths.SchemaVersion, ["kind"] = "dlc-content-export",
            ["platform"] = r.platform, ["appVersion"] = r.appVersion, ["addressablesProfile"] = r.profileName,
            ["remoteLoadPath"] = r.remoteLoadPath, ["addressablesBuildPath"] = r.buildPath,
            ["catalogFile"] = r.catalogFile, ["hashFile"] = r.hashFile, ["catalogBuild"] = r.catalogBuild,
            ["contentStateFile"] = r.contentStateFile,
            ["exportedUtc"] = DateTime.UtcNow.ToString("o"), ["releases"] = new JArray(r.releaseIds),
            ["products"] = new JArray(r.products.Select(p => p.productId)),
            ["localOnlyProducts"] = new JArray(r.localOnlyProducts), ["ignoredLocalEntries"] = r.ignoredEntries };
        DlcReleaseService.WriteNew(Path.Combine(dir, "export.json"), export.ToString(Formatting.Indented));
        DlcReleaseService.WriteNew(Path.Combine(dir, "validation.json"),
            new JObject { ["errors"] = new JArray(r.errors), ["warnings"] = new JArray(r.warnings) }.ToString(Formatting.Indented));
        r.outputDirectory = dir;
    }
}
