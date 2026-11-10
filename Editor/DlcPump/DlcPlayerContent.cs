using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Arbelos.BuildUtility.Runtime;
using Force.Crc32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// The app's own Addressables output from a local build: Library/com.unity.addressables/aa/&lt;Platform&gt;
/// (settings.json, the local catalog, link.xml and the content built into the app). Unity copies that folder into
/// every player build, so a player build needs it but doesn't have to build Addressables itself.
/// <list type="bullet">
/// <item>The DLC content export packs it as player-addressables.zip, with the remote build's CRCs (GameAddressableData).</item>
/// <item>publish_dlc.py stores it in the staging container at _build-state/&lt;Platform&gt;/&lt;appVersion&gt;/player-addressables.zip.</item>
/// <item>Unity Cloud Build's pre-export calls <see cref="RestoreForCloudBuild"/> instead of building Addressables, so
/// the app ships exactly the build whose catalog was published.</item>
/// </list>
/// </summary>
public static class DlcPlayerContent
{
    public const string FileName = "player-addressables.zip";
    const string ManifestName = "player.json";
    const string AaPrefix = "aa/";
    static readonly DateTimeOffset FixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The folder Unity copies into the player (Library/com.unity.addressables/aa/&lt;Platform&gt;).</summary>
    public static string BuildPath => Path.GetFullPath(UnityEngine.AddressableAssets.Addressables.BuildPath);

    public static string BlobName(string platform, string appVersion) => $"_build-state/{platform}/{appVersion}/{FileName}";

    /// <summary>Null when the local Addressables player output belongs to <paramref name="catalogFile"/>, else why not.</summary>
    public static string Check(string folder, string catalogFile)
    {
        string settings = Path.Combine(folder, "settings.json");
        if (!File.Exists(settings)) return $"There's no Addressables player output at {folder}. Build Addressables for this platform first.";
        string name = Path.GetFileNameWithoutExtension(catalogFile);
        if (!File.ReadAllText(settings).Contains(name + ".hash"))
            return $"{settings} is from another Addressables build: it doesn't point at {name}. Build Addressables again, then export.";
        return null;
    }

    /// <summary>
    /// Writes the package: player.json (platform, app version, catalog and the CRCs of the remote build's files) and
    /// aa/&lt;files&gt;. Entries are sorted and have a fixed time, so the same build gives the same zip.
    /// </summary>
    public static void Write(string zipPath, string aaFolder, string platform, string appVersion, string catalogFile, string catalogBuild, string remoteBuildPath)
    {
        var crc = new JArray(new DirectoryInfo(remoteBuildPath).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new JObject { ["key"] = f.Name, ["value"] = Crc32CAlgorithm.Compute(File.ReadAllBytes(f.FullName)) }));
        var manifest = new JObject {
            ["schemaVersion"] = 1, ["platform"] = platform, ["appVersion"] = appVersion,
            ["catalogFile"] = catalogFile, ["catalogBuild"] = catalogBuild, ["crc"] = crc };
        string root = Path.GetFullPath(aaFolder).TrimEnd('\\', '/');
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (full: f, rel: f.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')))
            .OrderBy(f => f.rel, StringComparer.Ordinal).ToList();
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, ManifestName, Encoding.UTF8.GetBytes(manifest.ToString(Formatting.Indented)));
            foreach (var f in files) Add(zip, AaPrefix + f.rel, File.ReadAllBytes(f.full));
        }
    }

    static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTime;
        using (var s = entry.Open()) s.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Unpacks a package into <paramref name="aaFolder"/> (replacing it) after checking it's for this platform and
    /// app version. Returns the manifest (its "crc" array is for GameAddressableData).
    /// </summary>
    public static JObject Extract(byte[] zipData, string aaFolder, string platform, string appVersion)
    {
        using (var zip = new ZipArchive(new MemoryStream(zipData), ZipArchiveMode.Read))
        {
            var entry = zip.GetEntry(ManifestName) ?? throw new BuildFailedException($"{FileName} has no {ManifestName}.");
            JObject manifest;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) manifest = JObject.Parse(reader.ReadToEnd());
            if ((int?)manifest["schemaVersion"] != 1) throw new BuildFailedException($"{FileName} has an unsupported schemaVersion.");
            if ((string)manifest["platform"] != platform || (string)manifest["appVersion"] != appVersion)
                throw new BuildFailedException($"{FileName} is for {manifest["platform"]} {manifest["appVersion"]}, not {platform} {appVersion}.");

            string root = Path.GetFullPath(aaFolder).TrimEnd('\\', '/');
            string temp = root + ".restore";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(AaPrefix, StringComparison.Ordinal) && !e.FullName.EndsWith("/")))
            {
                string rel = e.FullName.Substring(AaPrefix.Length);
                string dest = Path.GetFullPath(Path.Combine(temp, rel));
                if (rel.Length == 0 || Path.IsPathRooted(rel) || !dest.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new BuildFailedException($"{FileName} has an unsafe path: {e.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (var input = e.Open())
                using (var output = File.Create(dest)) input.CopyTo(output);
            }
            string problem = Check(temp, (string)manifest["catalogFile"]);
            if (problem != null) { Directory.Delete(temp, true); throw new BuildFailedException(FileName + ": " + problem); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.GetDirectoryName(root));
            Directory.Move(temp, root);
            return manifest;
        }
    }

    /// <summary>
    /// Unity Cloud Build pre-export, in place of building Addressables: downloads the package stored for the active
    /// platform and PlayerSettings.bundleVersion from the staging container and unpacks it where Unity picks it up for
    /// the player. Also writes its CRCs to the GameAddressableData asset in Resources, as the old CRC step did.
    /// <para>Storage: the DlcPublishingSettings asset's account and staging container, or DLC_STORAGE_ACCOUNT /
    /// DLC_STORAGE_CONTAINER. Sign-in: a service principal from DLC_AZURE_TENANT_ID, DLC_AZURE_CLIENT_ID,
    /// DLC_AZURE_CLIENT_SECRET (or TENANT_ID, CLIENT_ID, CLIENT_SECRET) with Storage Blob Data Reader on the
    /// container; with none of them set, the blob is read anonymously.</para>
    /// </summary>
    public static void RestoreForCloudBuild()
    {
        string platform = DlcContentPublisher.CurrentPlatform, appVersion = DlcContentPublisher.CurrentAppVersion;
        var publishing = AssetDatabase.FindAssets("t:" + nameof(DlcPublishingSettings)).Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<DlcPublishingSettings>).Where(s => s).ToList();
        string account = Env("DLC_STORAGE_ACCOUNT"), container = Env("DLC_STORAGE_CONTAINER");
        if (account == null || container == null)
        {
            if (publishing.Count != 1)
                throw new BuildFailedException($"Found {publishing.Count} DlcPublishingSettings assets. Keep one, or set DLC_STORAGE_ACCOUNT and DLC_STORAGE_CONTAINER.");
            account = account ?? publishing[0].accountName?.Trim();
            container = container ?? publishing[0].container?.Trim();
        }
        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(container)) throw new BuildFailedException("No storage account or staging container for the app's Addressables.");

        string blob = BlobName(platform, appVersion);
        string url = $"https://{account}.blob.core.windows.net/{container}/{blob}";
        Debug.Log($"Restoring the app's Addressables from {url} (no Addressables build in the cloud).");
        byte[] data = Download(url, platform, appVersion);
        var manifest = Extract(data, BuildPath, platform, appVersion);
        int crcs = WriteCrcs(manifest["crc"] as JArray);
        Debug.Log($"Restored {BuildPath} for {platform} {appVersion} (catalog {manifest["catalogFile"]}, build {manifest["catalogBuild"]})" +
                  (crcs >= 0 ? $"; {crcs} CRC(s) written to GameAddressableData." : "; no GameAddressableData in Resources, CRCs skipped."));
    }

    static int WriteCrcs(JArray crc)
    {
        var data = AssetDatabase.FindAssets("t:" + nameof(GameAddressableData)).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.Replace('\\', '/').Contains("/Resources/")).Select(AssetDatabase.LoadAssetAtPath<GameAddressableData>).FirstOrDefault(d => d);
        if (!data) return -1;
        data.AddressableCRCList.Clear();
        foreach (var item in crc ?? new JArray()) data.AddressableCRCList.Add(new AddressableCRCEntry((string)item["key"], (uint)item["value"]));
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
        return data.AddressableCRCList.Count;
    }

    static string Env(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static byte[] Download(string url, string platform, string appVersion)
    {
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            string token = Token(http);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("x-ms-version", "2021-08-06");
                    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = http.SendAsync(request).GetAwaiter().GetResult())
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                            throw new BuildFailedException($"No app Addressables stored for {platform} {appVersion} ({url}). Build Addressables for {platform} " +
                                                           $"with app version {appVersion} on your machine, then export and publish with DLC Publish: the publish stores them.");
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                            throw new BuildFailedException($"Not allowed to read {url} ({(int)response.StatusCode}). " +
                                                           (token == null ? "Set DLC_AZURE_TENANT_ID, DLC_AZURE_CLIENT_ID and DLC_AZURE_CLIENT_SECRET (a service principal) in the build's environment variables."
                                                                          : "Give the service principal Storage Blob Data Reader on the container."));
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception e) when (!(e is BuildFailedException) && attempt < 3)
                {
                    Debug.LogWarning($"Downloading {url} failed ({e.Message}); retrying.");
                    System.Threading.Thread.Sleep(3000 * attempt);
                }
                catch (Exception e) when (!(e is BuildFailedException))
                {
                    throw new BuildFailedException($"Couldn't download {url}: {e.Message}");
                }
            }
        }
    }

    static string Token(HttpClient http)
    {
        string tenant = Env("DLC_AZURE_TENANT_ID") ?? Env("TENANT_ID");
        string client = Env("DLC_AZURE_CLIENT_ID") ?? Env("CLIENT_ID");
        string secret = Env("DLC_AZURE_CLIENT_SECRET") ?? Env("CLIENT_SECRET");
        if (tenant == null && client == null && secret == null) return null;
        if (tenant == null || client == null || secret == null)
            throw new BuildFailedException("Set all three of DLC_AZURE_TENANT_ID, DLC_AZURE_CLIENT_ID and DLC_AZURE_CLIENT_SECRET (or TENANT_ID, CLIENT_ID, CLIENT_SECRET).");
        var form = new FormUrlEncodedContent(new[] {
            new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "client_credentials"),
            new System.Collections.Generic.KeyValuePair<string, string>("client_id", client),
            new System.Collections.Generic.KeyValuePair<string, string>("client_secret", secret),
            new System.Collections.Generic.KeyValuePair<string, string>("scope", "https://storage.azure.com/.default") });
        using (var response = http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", form).GetAwaiter().GetResult())
        {
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string token = null;
            try { token = (string)JObject.Parse(body)["access_token"]; } catch (JsonException) { }
            if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(token))
            {
                string reason = null;
                try { reason = (string)JObject.Parse(body)["error_description"]; } catch (JsonException) { }
                throw new BuildFailedException($"Azure sign-in for the service principal failed ({(int)response.StatusCode}): {reason ?? "no access token"}");
            }
            return token;
        }
    }
}
