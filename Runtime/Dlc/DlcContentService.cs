using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace Arbelos.BuildUtility.Runtime
{
    public enum DlcProductStatus
    {
        /// <summary>Manifest loaded and every key is in the loaded Addressables catalog. A product with no keys
        /// (content that ships in the app) is Ready with an empty key list.</summary>
        Ready,
        /// <summary>Manifest loaded, but some keys are still missing after one catalog update.</summary>
        NotReady,
        /// <summary>products.json for this platform and app version doesn't list the product.</summary>
        NotInAppVersion,
        /// <summary>The manifest's minimumClientVersion is newer than Application.version.</summary>
        RequiresNewerClient,
        /// <summary>No usable manifest, online or cached.</summary>
        Unavailable,
    }

    public sealed class DlcProductState
    {
        public string ProductId { get; internal set; }
        public DlcProductStatus Status { get; internal set; }
        /// <summary>Version folder in use, e.g. "v2". Null when no manifest was loaded.</summary>
        public string Version { get; internal set; }
        public IReadOnlyList<string> Keys { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> MissingKeys { get; internal set; } = Array.Empty<string>();
        /// <summary>True when the manifest or products.json came from the local cache instead of the network.</summary>
        public bool FromCache { get; internal set; }
        public string Message { get; internal set; } = "";
        internal DlcProductManifest Manifest;
    }

    /// <summary>
    /// Downloads dlc-content/&lt;Platform&gt;/&lt;Application.version&gt;/products.json and each owned product's product.json,
    /// caches them under Application.persistentDataPath, and maps productId to its Addressables keys.
    ///
    /// Owned product IDs come from the game's own entitlement code; this service doesn't decide ownership.
    /// Run it after Addressables has initialized (e.g. after AddressablesDownloader.onInitialized) so the two
    /// don't update catalogs at the same time. It never calls LoadContentCatalogAsync: keys are checked against
    /// the main remote catalog, which Addressables updates the normal way.
    /// </summary>
    public sealed class DlcContentService
    {
        readonly DlcRemoteConfig config;
        readonly string platform;
        readonly string appVersion;
        readonly string cacheRoot;
        Dictionary<string, DlcProductState> states = new Dictionary<string, DlcProductState>(StringComparer.Ordinal);
        Task<IReadOnlyDictionary<string, DlcProductState>> running;

        /// <summary>Required when <see cref="DlcRemoteConfig.accessMode"/> is SignedUrl. See <see cref="IDlcUrlSigner"/>.</summary>
        public IDlcUrlSigner UrlSigner { get; set; }

        public IReadOnlyDictionary<string, DlcProductState> Products => states;

        /// <summary>
        /// Every product published for this platform and app version, owned or not, from the last loaded
        /// products.json (empty before the first LoadAsync, or when no copy is available).
        /// </summary>
        public IReadOnlyList<string> PublishedProductIds { get; private set; } = Array.Empty<string>();

        /// <summary>True when products.json for this platform and app version lists the product.</summary>
        public bool IsPublished(string productId) => productId != null && PublishedProductIds.Contains(productId);

        public DlcContentService(DlcRemoteConfig config, string cacheRoot = null)
        {
            if (!config) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.cdnBaseUrl)) throw new ArgumentException("DlcRemoteConfig.cdnBaseUrl is empty.", nameof(config));
            this.config = config;
            platform = DlcContentPaths.CurrentPlatform();
            appVersion = Application.version;
            if (!DlcContentPaths.IsSafeSegment(platform) || !DlcContentPaths.IsSafeSegment(appVersion))
                throw new InvalidOperationException($"Platform '{platform}' or app version '{appVersion}' can't be used as a folder name.");
            this.cacheRoot = cacheRoot ?? Path.Combine(Application.persistentDataPath, "dlc-content", platform, appVersion);
        }

        /// <summary>Keys for a product whose status is Ready. The list is empty for local-only products.</summary>
        public bool TryGetKeys(string productId, out IReadOnlyList<string> keys)
        {
            if (productId != null && states.TryGetValue(productId, out var s) && s.Status == DlcProductStatus.Ready)
            {
                keys = s.Keys;
                return true;
            }
            keys = Array.Empty<string>();
            return false;
        }

        /// <summary>Loads manifests for the owned products. A second call while one is running returns the same task.</summary>
        public Task<IReadOnlyDictionary<string, DlcProductState>> LoadAsync(IEnumerable<string> ownedProductIds)
        {
            if (running != null && !running.IsCompleted) return running;
            running = LoadInternal((ownedProductIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList());
            return running;
        }

        async Task<IReadOnlyDictionary<string, DlcProductState>> LoadInternal(List<string> owned)
        {
            var result = new Dictionary<string, DlcProductState>(StringComparer.Ordinal);
            var (index, indexFromCache, indexError) = await LoadIndex();
            if (index != null)
                PublishedProductIds = (index.products ?? new List<DlcProductsIndexEntry>())
                    .Where(p => p != null && !string.IsNullOrEmpty(p.productId)).Select(p => p.productId).Distinct(StringComparer.Ordinal).ToList();

            foreach (string id in owned)
            {
                var state = new DlcProductState { ProductId = id };
                result[id] = state;
                if (index == null) { Fail(state, DlcProductStatus.Unavailable, indexError); continue; }
                if (!DlcContentPaths.IsSafeSegment(id)) { Fail(state, DlcProductStatus.Unavailable, "Product ID can't be used as a folder name."); continue; }

                var entry = index.products?.FirstOrDefault(p => p != null && p.productId == id);
                if (entry == null) { Fail(state, DlcProductStatus.NotInAppVersion, $"Not published for {platform} {appVersion}."); continue; }

                await LoadManifest(state, entry, indexFromCache);
            }

            await CheckKeys(result.Values.Where(s => s.Manifest != null && s.Status == DlcProductStatus.Ready).ToList());

            foreach (var s in result.Values)
                if (s.Status != DlcProductStatus.Ready)
                    Debug.LogWarning($"[DLC] {s.ProductId}: {s.Status}. {s.Message}");

            states = result;
            return result;
        }

        async Task<(DlcProductsIndex index, bool fromCache, string error)> LoadIndex()
        {
            string path = Path.Combine(cacheRoot, DlcContentPaths.ProductsIndexFile);
            string etagPath = path + ".etag";
            string cachedEtag = File.Exists(path) && File.Exists(etagPath) ? SafeRead(etagPath) : null;

            var response = await Get(DlcContentPaths.ProductsIndexFile, cachedEtag);
            DlcProductsIndex fresh = null;
            string parseError = null;
            if (response.Code == 200 && TryParseIndex(response.Text, out fresh, out parseError))
            {
                WriteAtomic(path, response.Text);
                if (!string.IsNullOrEmpty(response.ETag)) WriteAtomic(etagPath, response.ETag);
                else if (File.Exists(etagPath)) File.Delete(etagPath);
                return (fresh, false, null);
            }

            string reason = response.Code == 304 ? null
                : response.Code == 200 ? "products.json is invalid: " + parseError
                : response.Code == 404 ? $"No DLC published for {platform} {appVersion} (404)."
                : "products.json download failed: " + response.Error;

            if (File.Exists(path) && TryParseIndex(SafeRead(path), out var cached, out _))
            {
                if (reason != null) Debug.LogWarning($"[DLC] {reason} Using cached products.json.");
                // A 304 means the cache is current, so it doesn't count as offline.
                return (cached, response.Code != 304, null);
            }
            return (null, false, reason ?? "Server answered 304 but no cached products.json exists.");
        }

        bool TryParseIndex(string text, out DlcProductsIndex index, out string error)
        {
            index = null; error = null;
            try { index = JsonUtility.FromJson<DlcProductsIndex>(text); }
            catch (Exception e) { error = e.Message; return false; }
            if (index == null || index.schemaVersion != DlcContentPaths.SchemaVersion) { error = "unsupported schemaVersion"; return false; }
            if (index.platform != platform || index.appVersion != appVersion) { error = $"it is for {index.platform} {index.appVersion}"; return false; }
            return true;
        }

        async Task LoadManifest(DlcProductState state, DlcProductsIndexEntry entry, bool indexFromCache)
        {
            if (!DlcContentPaths.IsVersionFolder(entry.current)) { Fail(state, DlcProductStatus.Unavailable, $"Invalid current version '{entry.current}'."); return; }

            DlcProductManifest manifest = ReadCachedManifest(state.ProductId, entry.current);
            string version = entry.current;
            bool fromCache = indexFromCache || manifest != null;
            string downloadError = null;

            if (manifest == null)
            {
                // Version folders are immutable, so a cached copy never needs revalidating.
                var response = await Get(state.ProductId + "/" + entry.current + "/" + DlcContentPaths.ProductFile, null);
                string parseError = null;
                if (response.Code == 200 && TryParseManifest(response.Text, state.ProductId, entry.current, out manifest, out parseError))
                    WriteAtomic(ManifestPath(state.ProductId, entry.current), response.Text);
                else
                {
                    manifest = null;
                    downloadError = response.Code == 200 ? parseError : response.Error;
                }
            }

            if (manifest == null)
            {
                // Offline fallback: the newest cached version that products.json still lists for this app version.
                foreach (string older in (entry.versions ?? new List<string>()).Where(DlcContentPaths.IsVersionFolder))
                {
                    manifest = ReadCachedManifest(state.ProductId, older);
                    if (manifest != null) { version = older; fromCache = true; break; }
                }
            }

            if (manifest == null) { Fail(state, DlcProductStatus.Unavailable, $"product.json {entry.current} unavailable: {downloadError}"); return; }

            state.Manifest = manifest;
            state.Version = version;
            state.FromCache = fromCache;
            state.Keys = manifest.keys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
            if (version != entry.current) state.Message = $"Using cached {version}; {entry.current} couldn't be downloaded ({downloadError}).";

            if (!DlcVersion.Satisfies(appVersion, manifest.minimumClientVersion))
                Fail(state, DlcProductStatus.RequiresNewerClient, $"Requires app {manifest.minimumClientVersion}; this is {appVersion}.");
            else
                state.Status = DlcProductStatus.Ready;
        }

        DlcProductManifest ReadCachedManifest(string productId, string version)
        {
            string path = ManifestPath(productId, version);
            return File.Exists(path) && TryParseManifest(SafeRead(path), productId, version, out var m, out _) ? m : null;
        }

        bool TryParseManifest(string text, string productId, string version, out DlcProductManifest manifest, out string error)
        {
            manifest = null; error = null;
            try { manifest = JsonUtility.FromJson<DlcProductManifest>(text); }
            catch (Exception e) { error = e.Message; return false; }
            if (manifest == null || manifest.schemaVersion != DlcContentPaths.SchemaVersion) { error = "unsupported schemaVersion"; manifest = null; return false; }
            if (manifest.productId != productId || "v" + manifest.version != version) { error = $"manifest is for {manifest.productId} v{manifest.version}"; manifest = null; return false; }
            if (manifest.platform != platform || manifest.appVersion != appVersion) { error = $"manifest is for {manifest.platform} {manifest.appVersion}"; manifest = null; return false; }
            manifest.keys = manifest.keys ?? new List<string>();
            return true;
        }

        async Task CheckKeys(List<DlcProductState> candidates)
        {
            if (candidates.Count == 0) return;
            var init = Addressables.InitializeAsync(false);
            await init.Task;
            Addressables.Release(init);

            if (await FindMissing(candidates))
            {
                // Once per load: pull the newest main catalog, then check again.
                var check = Addressables.CheckForCatalogUpdates(false);
                await check.Task;
                var updatable = check.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && check.Result != null
                    ? new List<string>(check.Result) : new List<string>();
                Addressables.Release(check);
                if (updatable.Count > 0)
                {
                    var update = Addressables.UpdateCatalogs(updatable, false);
                    await update.Task;
                    Addressables.Release(update);
                }
                await FindMissing(candidates);
            }

            foreach (var s in candidates.Where(s => s.MissingKeys.Count > 0))
                Fail(s, DlcProductStatus.NotReady,
                    $"{s.MissingKeys.Count} key(s) not in the loaded catalog after a catalog update, e.g. '{s.MissingKeys[0]}'.");
        }

        static async Task<bool> FindMissing(List<DlcProductState> candidates)
        {
            bool any = false;
            foreach (var s in candidates)
            {
                var missing = new List<string>();
                foreach (string key in s.Keys)
                {
                    var handle = Addressables.LoadResourceLocationsAsync(key);
                    await handle.Task;
                    bool found = handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded
                                 && handle.Result != null && handle.Result.Count > 0;
                    Addressables.Release(handle);
                    if (!found) missing.Add(key);
                }
                s.MissingKeys = missing;
                any |= missing.Count > 0;
            }
            return any;
        }

        static void Fail(DlcProductState state, DlcProductStatus status, string message)
        {
            state.Status = status;
            state.Message = string.IsNullOrEmpty(state.Message) ? message : state.Message + " " + message;
        }

        string ManifestPath(string productId, string version) => Path.Combine(cacheRoot, productId, version, DlcContentPaths.ProductFile);

        // ------------------------------------------------------------------ HTTP

        struct Response { public long Code; public string Text; public string ETag; public string Error; }

        string Url(string relative)
        {
            string url = DlcContentPaths.AppVersionFolderUrl(config.cdnBaseUrl, platform, appVersion) + relative;
            if (config.accessMode == DlcContentAccessMode.SignedUrl)
            {
                if (UrlSigner == null)
                    throw new InvalidOperationException("DlcRemoteConfig.accessMode is SignedUrl but no IDlcUrlSigner is set. Signed URLs aren't implemented yet; see IDlcUrlSigner.");
                url = UrlSigner.Sign(url);
            }
            return url;
        }

        async Task<Response> Get(string relative, string etag)
        {
            using (var request = UnityWebRequest.Get(Url(relative)))
            {
                request.timeout = Math.Max(1, config.requestTimeoutSeconds);
                if (!string.IsNullOrEmpty(etag)) request.SetRequestHeader("If-None-Match", etag);
                var op = request.SendWebRequest();
                var done = new TaskCompletionSource<bool>();
                if (op.isDone) done.TrySetResult(true);
                else op.completed += _ => done.TrySetResult(true);
                await done.Task;

                long code = request.responseCode;
                if (code == 304) return new Response { Code = 304 };
                if (request.result == UnityWebRequest.Result.Success && code == 200)
                    return new Response { Code = 200, Text = request.downloadHandler.text, ETag = request.GetResponseHeader("ETag") };
                return new Response { Code = code, Error = string.IsNullOrEmpty(request.error) ? "HTTP " + code : request.error };
            }
        }

        // ------------------------------------------------------------------ Cache files

        static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return null; }
        }

        static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DLC] Couldn't cache {path}: {e.Message}");
            }
        }
    }
}
