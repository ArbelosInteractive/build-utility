using System.Linq;
using System.IO;
using UnityEngine;
using System.Threading.Tasks;
using UnityEngine.Events;
using UnityEngine.AddressableAssets;
using System.Collections.Generic;
using UnityEngine.AddressableAssets.ResourceLocators;
using Force.Crc32;
using UnityEngine.ResourceManagement.AsyncOperations;
using System;
using System.Collections;
using System.Threading;

namespace Arbelos.BuildUtility.Runtime
{
    public class AddressablesDownloader : MonoBehaviour
    {
        #region public variables

        [HideInInspector] public bool isInitialized;

        [Tooltip("List of Asset names to skip the download for. It is case sensitive.")]
        public List<string> assetsToSkip = new();
        public int numDownloaded;
        public int numAssetBundlesToDownload;
        public float percentageDownloaded;
        public UnityEvent onInitialized;
        public UnityEvent onUpdateAvailable;
        public UnityEvent onDownloadStarted;
        public UnityEvent onValidationFail;
        public UnityEvent onCustomContentCatalogLoaded;
        public UnityEvent<float> onPercentageDownloaded;
        public GameAddressableData addressableData;

        private readonly List<object> downloadedKeys = new();
        private List<object> pendingKeys = new();
        private bool wasConnected = true;
        private bool wasPaused;
        private bool hasError = false;
        
        // Used to track that initial addressables initialization code has been run.
        private bool addressablesInitialized;
        private bool downloadInitialized;

        private bool assetDownloadActive = false;
        private CancellationTokenSource downloadCancellationTokenSource;
        
        #endregion
        
        #region MonoBehaviour

        void OnApplicationPause(bool pauseStatus)
        {
            if (isInitialized || !addressablesInitialized)
                return;

            if (pauseStatus != wasPaused && wasConnected)
            {
                wasPaused = pauseStatus;
                if (pauseStatus)
                {
                    Debug.Log("[Addressables Downloader] App paused (device may be sleeping or switching apps)");
                    CancelDownload();
                }
                else
                {
                    Debug.Log("[Addressables Downloader] App resumed");
                    ResumePendingDownload();
                }
            }
        }
        
        IEnumerator CheckInternet()
        {
            while (!isInitialized)
            {
                // Waits for addressables and first download to initialize
                if (!addressablesInitialized || !downloadInitialized)
                {
                    yield return new WaitForSeconds(5f); // check every 5 seconds
                }
                
                bool isConnected = Application.internetReachability != NetworkReachability.NotReachable;
                if (isConnected != wasConnected)
                {
                    wasConnected = isConnected;
                    if (isConnected)
                    {                    
                        Debug.Log("[Addressables Downloader] Internet connected");
                        ResumePendingDownload();    
                    }
                    else
                    {
                        Debug.Log("[Addressables Downloader] Internet disconnected");
                        CancelDownload();
                    }
                }
                
                yield return new WaitForSeconds(5f); // check every 5 seconds
            }
        }

        protected static float BytesToKiloBytes(long bytes)
        {
            return bytes / 1024f;
        }

        #endregion

        #region Key gathering helpers

        /// <summary>
        /// Gathers all unique keys from the given resource locators.
        /// </summary>
        private static List<object> BuildKeyListFromLocators(IEnumerable<IResourceLocator> locators)
        {
            if (locators == null) return new List<object>();

            return locators
                .SelectMany(l => l.Keys)
                .Where(k => k != null)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Common routine to build pending keys from a set of locators.
        /// Clears state, fills pendingKeys.
        /// </summary>
        private void PreparePendingKeysFromLocators(IEnumerable<IResourceLocator> locators)
        {
            downloadedKeys.Clear();
            pendingKeys.Clear();

            var allKeys = BuildKeyListFromLocators(locators);
            pendingKeys = allKeys.ToList();
        }

        #endregion

        #region Catalog update + download

        /// <summary>
        /// Updates any remote catalogs (BuildContentUpdate support) and then downloads
        /// dependencies for the updated locators. If no locators are updated, falls back
        /// to downloading from current Addressables.ResourceLocators.
        /// </summary>
        public async Task<bool> UpdateAndDownload()
        {
            // Update all catalogs that need updating (null = all tracked catalogs).
            AsyncOperationHandle<List<IResourceLocator>> handle = Addressables.UpdateCatalogs(true, null, false);
            await handle.Task;

            List<IResourceLocator> updatedResourceLocators = handle.Result;
            Addressables.Release(handle);
            
            // Clear local state
            pendingKeys.Clear();
            downloadedKeys.Clear();

            IEnumerable<IResourceLocator> locatorsToUse = updatedResourceLocators;

            if (updatedResourceLocators != null && updatedResourceLocators.Count > 0)
            {
                onUpdateAvailable?.Invoke();
                Debug.Log($"[Addressables Downloader] {updatedResourceLocators.Count} catalog(s) updated.");
            }
            else
            {
                // No catalogs were updated – use current resource locators.
                locatorsToUse = Addressables.ResourceLocators;
                Debug.Log("[Addressables Downloader] No catalogs updated via UpdateCatalogs. Using current ResourceLocators for download.");
            }

            PreparePendingKeysFromLocators(locatorsToUse);

            CancelDownload();

            var tcs = new TaskCompletionSource<bool>();
            DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

            return await tcs.Task;
        }

        /// <summary>
        /// Downloads all content reachable from current Addressables.ResourceLocators.
        /// Used for fresh installs or if no catalog updates are found.
        /// </summary>
        private async Task<bool> DownloadAllCurrentContent()
        {
            var existingLocators = Addressables.ResourceLocators.ToList();

            if (existingLocators == null || existingLocators.Count == 0)
            {
                Debug.LogWarning("[Addressables Downloader] No ResourceLocators available to build download keys from.");
                return true;
            }

            PreparePendingKeysFromLocators(existingLocators);

            CancelDownload();

            var tcs = new TaskCompletionSource<bool>();
            DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

            return await tcs.Task;
        }

        #endregion

        #region Initialization

        public async void Initialize()
        {
            wasConnected = Application.internetReachability != NetworkReachability.NotReachable;
            StartCoroutine(CheckInternet());
            
            // wait for caching to get ready
            while (!Caching.ready)
            {
                await Task.Delay(1000);
            }
            
#if !UNITY_EDITOR
            addressableData = Resources.Load<GameAddressableData>("GameAddressableData");
#endif
            // Refresh Directories before doing anything
            RefreshCacheAndCatalogDirectories();

            AsyncOperationHandle<IResourceLocator> handle = Addressables.InitializeAsync(false);

            await handle.Task;

            addressablesInitialized = true;

            Addressables.Release(handle);
            
#if !UNITY_EDITOR
            if (addressableData != null && !string.IsNullOrEmpty(addressableData.profileName))
            {
                var profileType = addressableData.profileName;
                if (profileType != "EditorHosted")
                {
                    // NOTE:
                    // - We still call CheckForCatalogUpdates to know if an update is available,
                    //   BUT we no longer depend on "count > 0" to decide whether to download.
                    AsyncOperationHandle<List<string>> catalogHandle = Addressables.CheckForCatalogUpdates(false);
                    await catalogHandle.Task;

                    List<string> possibleUpdates = catalogHandle.Result;
                    Addressables.Release(catalogHandle);

                    bool downloadDone = false;

                    if (possibleUpdates != null && possibleUpdates.Count > 0)
                    {
                        Debug.Log("[Addressables Downloader] Catalog updates reported by CheckForCatalogUpdates.");
                        // This will update catalogs and then download content for updated locators,
                        // or fall back to current locators if nothing updated.
                        downloadDone = await UpdateAndDownload();
                    }
                    else
                    {
                        // No catalog updates.
                        // Decide whether this is a first launch (no initial download yet) or a subsequent launch.
                        string flagKey = GetInitialDownloadFlagKey();
                        bool initialDownloadDoneFlag = PlayerPrefs.GetInt(flagKey, 0) == 1;

                        if (!initialDownloadDoneFlag)
                        {
                            // First run (or cache was cleared): we need to actually download remote content.
                            Debug.Log("[Addressables Downloader] No catalog updates reported, and no initial download recorded. Downloading current content for fresh install / cache fill.");
                            downloadDone = await DownloadAllCurrentContent();
                        }
                        else
                        {
                            // We've already done an initial download for this profile and there are no catalog updates.
                            // Trust Addressables' caching. No need to re-download everything again.
                            Debug.Log("[Addressables Downloader] No catalog updates and initial download already completed. Skipping download.");
                            downloadDone = true;
                        }
                    }

                    downloadInitialized = true;

                    // Validate downloaded game files (catalog validation removed per 2.0+ changes).
                    if (downloadDone && ValidateCurrentlyDownloadedFiles())
                    {
                        isInitialized = true;

                        // Mark that we've successfully completed the initial download for this profile.
                        string flagKey = GetInitialDownloadFlagKey();
                        PlayerPrefs.SetInt(flagKey, 1);
                        PlayerPrefs.Save();

                        onInitialized?.Invoke();
                    }
                }
                else
                {
                    isInitialized = true;
                    onInitialized?.Invoke();
                }
            }
            else
            {
                Debug.LogError("[Addressables Downloader] Addressable Profile Data not found, please build correctly!");
            }
#endif
#if UNITY_EDITOR
            isInitialized = true;
            onInitialized?.Invoke();
#endif
        }

        #endregion

        #region Validation (game files only)
        
        private string GetInitialDownloadFlagKey()
        {
            string profile = (addressableData != null && !string.IsNullOrEmpty(addressableData.profileName))
                ? addressableData.profileName
                : "Default";

            return $"ADDR_INIT_DONE_{profile}";
        }

        private bool ValidateCurrentlyDownloadedFiles()
        {
#if !UNITY_EDITOR
            // NOTE:
            // Catalog validation is removed due to changes in catalog caching with Addressables 2.x.
            // We still validate game/bundle files using stored CRCs.

            if (addressableData == null || addressableData.AddressableCRCList == null || addressableData.AddressableCRCList.Count == 0)
            {
                Debug.LogWarning("[Addressables Downloader] No CRC data in GameAddressableData; skipping validation.");
                return true;
            }

            // Get the primary cache path (bundle cache).
            List<string> cachePaths = new List<string>();
            Caching.GetAllCachePaths(cachePaths);

            if (cachePaths == null || cachePaths.Count == 0)
            {
                Debug.LogWarning("[Addressables Downloader] No cache paths found; skipping game file validation.");
                return true;
            }

            string cachePath = cachePaths[0];

            // File IDs will be used to search for folders in cachePath assets that are under the same folder names as the assetFileIDs
            List<string> assetsFileIds = FetchGameAssetsFileIds(addressableData.AddressableCRCList);

            if (!ValidateGameFiles(addressableData.AddressableCRCList, assetsFileIds, cachePath))
            {
                onValidationFail?.Invoke();
                StartReDownload();
                Debug.Log("<color=orange>INVALID GAME FILES DETECTED!!</color>");
                return false;
            }
#endif
            return true;
        }

        #endregion

        #region Redownload / Resume

        private async void RedownloadGameFiles()
        {
            var existingLocators = Addressables.ResourceLocators.ToList();

            PreparePendingKeysFromLocators(existingLocators);

            CancelDownload();

            var tcs = new TaskCompletionSource<bool>();
            DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

            var downloadDone = await tcs.Task;

            // validate files
            if (downloadDone && ValidateCurrentlyDownloadedFiles())
            {
                isInitialized = true;
                onInitialized?.Invoke();
            }
        }

        private async void ResumePendingDownload()
        {
            Debug.Log("[Addressables Downloader] Resuming download...");
            
            // Remove any stragglers already existing in downloaded keys.
            pendingKeys = pendingKeys.Distinct().Except(downloadedKeys).ToList();
            
            CancelDownload();

            var tcs = new TaskCompletionSource<bool>();
            DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

            var downloadDone = await tcs.Task;

            // validate files
            if (downloadDone && ValidateCurrentlyDownloadedFiles())
            {
                isInitialized = true;
                onInitialized?.Invoke();
            }
        }

        private void StartReDownload()
        {
            // Initialize the download again if corrupted files!
            RedownloadGameFiles();
        }
        
        #endregion

        #region Download API

        // Call this to cancel the download
        public void CancelDownload()
        {
            if (downloadCancellationTokenSource != null && !downloadCancellationTokenSource.IsCancellationRequested)
            {
                downloadCancellationTokenSource.Cancel();
                Debug.Log("[Addressables Downloader] Cancellation requested.");
            }
        }

        public async Task DownloadKeysAsync(List<object> _keys, Action<bool> onComplete)
        {
            assetDownloadActive = true;
            downloadCancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = downloadCancellationTokenSource.Token;

            hasError = false;
            bool result = false;

            if (hasError || wasPaused || !wasConnected)
            {
                Debug.Log("[Addressables Downloader] Exiting Download... Either has error, paused, or not connected.");
                assetDownloadActive = false;
                onComplete?.Invoke(false);
                return;
            }

            // All bundles we care about in this pass:
            numAssetBundlesToDownload = pendingKeys.Count + downloadedKeys.Count;
            numDownloaded = downloadedKeys.Count;

            try
            {
                onDownloadStarted?.Invoke();
                foreach (var key in _keys.ToArray())
                {
                    token.ThrowIfCancellationRequested();

                    if (hasError || wasPaused || !wasConnected)
                    {
                        Debug.Log("[Addressables Downloader] Exiting Download... Either cancelled, error, paused or not connected.");
                        assetDownloadActive = false;
                        onComplete?.Invoke(false);
                        return;
                    }
                    
                    // Skip download if selected to be skipped.
                    if (assetsToSkip.Exists(x => x.Contains(key.ToString())))
                    {
                        Debug.Log($"[Addressables Downloader] {key} key was skipped as requested.");
                        
                        if (!pendingKeys.Remove(key))
                            Debug.Log($"[Addressables Downloader] {key} key was not removed from Pending Keys");

                        downloadedKeys.Add(key);

                        numDownloaded++;
                        percentageDownloaded = (float)numDownloaded / numAssetBundlesToDownload * 100f;
                        onPercentageDownloaded?.Invoke(percentageDownloaded);
                        continue;
                    }

                    // NOTE:
                    // - GetDownloadSizeAsync is no longer used, as it often returns 0 in 2.x
                    //   even when downloads are needed (especially with shared bundles).
                    // - We also no longer clear dependency cache per key; Addressables is
                    //   responsible for determining what actually needs downloading.

                    Debug.Log($"[Addressables Downloader] Starting download for Key: {key}.");
                    AsyncOperationHandle downloadHandle = Addressables.DownloadDependenciesAsync(key);
                    await downloadHandle.Task;

                    if (downloadHandle.Status == AsyncOperationStatus.Succeeded)
                    {
                        if (!pendingKeys.Remove(key))
                            Debug.Log($"[Addressables Downloader] {key} key was not removed from Pending Keys");

                        Debug.Log($"[Addressables Downloader] Download Completed for Key: {key}.");
                        downloadedKeys.Add(key);
                    }
                    else
                    {
                        hasError = true;
                        Debug.LogError($"[Addressables Downloader] Download failed for key: {key}");
                        Addressables.Release(downloadHandle);
                        continue;
                    }

                    Addressables.Release(downloadHandle);

                    numDownloaded++;
                    percentageDownloaded = (float)numDownloaded / numAssetBundlesToDownload * 100f;
                    onPercentageDownloaded?.Invoke(percentageDownloaded);
                }

                if (!hasError && !wasPaused && wasConnected)
                {
                    Debug.Log("[Addressables Downloader] All downloads completed successfully.");
                    result = true;
                }
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[Addressables Downloader] Download was cancelled.");
                hasError = true;
                assetDownloadActive = false;
                result = false;
            }
            finally
            {
                assetDownloadActive = false;
                onComplete?.Invoke(result);
                downloadCancellationTokenSource.Dispose();
                downloadCancellationTokenSource = null;
            }
        }
        
        #endregion

        #region Cache refresh / helpers

        private void RefreshCacheAndCatalogDirectories()
        {
            // Refresh the catalog directory.
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);
                dir.Refresh();
            }

            // Refresh Cache Directory
            List<string> cachePaths = new List<string>();
            Caching.GetAllCachePaths(cachePaths);
            if (cachePaths.Count > 0)
            {
                string cachePath = cachePaths[0];
                if (Directory.Exists(cachePath))
                {
                    DirectoryInfo dir = new DirectoryInfo(cachePath);
                    dir.Refresh();
                }
            }
        }

        private List<string> FetchGameAssetsFileIds(List<AddressableCRCEntry> data)
        {
            var assetFileIds = new List<string>();

            if (data == null)
                return assetFileIds;

            foreach (var entry in data)
            {
                //Skip catalog files.
                if (entry == null || string.IsNullOrEmpty(entry.key) || entry.key.Contains("catalog"))
                    continue;

                string fileName = entry.key;
                
                // Unity built-in shaders bundle special-case
                if (fileName.Contains("unitybuiltinshaders"))
                {
                    // Example patterns: "abcd_unitybuiltinshaders.bundle" or similar
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var parts = nameWithoutExt.Split('_');

                    // Be defensive – only build id if we have at least 2 segments
                    if (parts.Length >= 2)
                    {
                        // Historically you used "parts[0] + _unitybuiltinshaders" as folder name
                        string finalName = parts[0] + "_unitybuiltinshaders";
                        assetFileIds.Add(finalName);
                    }

                    continue;
                }
                
                // Unity monoscripts bundle special-case
                if (fileName.Contains("monoscripts"))
                {
                    // Example patterns: "abcd_monoscripts.bundle" or similar
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var parts = nameWithoutExt.Split('_');

                    // Be defensive – only build id if we have at least 2 segments
                    if (parts.Length >= 2)
                    {
                        // Historically you used "parts[0] + _monoscripts" as folder name
                        string finalName = parts[0] + "_monoscripts";
                        assetFileIds.Add(finalName);
                    }

                    continue;
                }
                
                // Unity built-in assets bundle special-case
                if (fileName.Contains("unitybuiltinassets"))
                {
                    // Example patterns: "abcd_monoscripts.bundle" or similar
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var parts = nameWithoutExt.Split('_');

                    // Be defensive – only build id if we have at least 2 segments
                    if (parts.Length >= 2)
                    {
                        // Historically you used "parts[0] + _unitybuiltinassets" as folder name
                        string finalName = parts[0] + "_unitybuiltinassets";
                        assetFileIds.Add(finalName);
                    }

                    continue;
                }
                
                // General case for "normal" bundles
                // e.g. "group_something_f59db7a2af3be597e715cca63b051863.bundle"
                // or "group_f59db7a2af3be597e715cca63b051863.bundle"
                string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
                if (string.IsNullOrEmpty(withoutExtension))
                    continue;

                string[] partsGeneral = withoutExtension.Split('_');
                if (partsGeneral.Length == 0)
                    continue;

                // Use the last segment as the file id (hash)
                string id = partsGeneral[partsGeneral.Length - 1];
                if (!string.IsNullOrEmpty(id))
                {
                    assetFileIds.Add(id);
                }
            }

            return assetFileIds;
        }
        
        private bool ValidateGameFiles(List<AddressableCRCEntry> _data, List<string> _fileIds, string _cachePath)
        {
            bool isValid = false;

            List<DirectoryInfo> assetFolders = FindAssetFolders(_fileIds, _cachePath);
            
            if (assetFolders.Count > 0 && assetFolders.Count == _fileIds.Count)
            {
                foreach (var folder in assetFolders)
                {
                    foreach (var data in _data)
                    {
                        if (data.key.Contains(folder.Name))
                        {
                            // Check if a sub folder exists
                            var subDirs = folder.GetDirectories();
                            if (subDirs.Length > 0)
                            {
                                var files = subDirs.First().GetFiles();
                                if (files.Length < 2)
                                {
                                    Debug.LogError($"[Addressables Downloader] No minimum required amount of files (2) found under the sub direc. folder of key: {data.key}");
                                    return false;
                                }
                                else
                                {
                                    foreach (var file in files)
                                    {
                                        if (file.Name.Contains("data"))
                                        {
                                            uint value = CalculateCRCValue(file);
                                            if (value == data.value)
                                            {
                                                isValid = true;
                                            }
                                            else
                                            {
                                                Debug.LogError($"[Addressables Downloader] CRC Check failed for {data.key} key. File is invalid.");
                                                return false;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var files = folder.GetFiles();
                                if (files.Length < 2)
                                {
                                    Debug.LogError($"[Addressables Downloader] No minimum required amount of files (2) found under the folder of key: {data.key}");
                                    return false;
                                }
                                else
                                {
                                    foreach (var file in files)
                                    {
                                        if (file.Name.Contains("data"))
                                        {
                                            uint value = CalculateCRCValue(file);
                                            if (value == data.value)
                                            {
                                                isValid = true;
                                            }
                                            else
                                            {
                                                Debug.LogError($"[Addressables Downloader] CRC Check failed for {data.key} key. File is invalid.");
                                                return false;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                Debug.LogError("[Addressables Downloader] Not all game file asset folders found!");
                return false;
            }

            return isValid;
        }

        private List<DirectoryInfo> FindAssetFolders(List<string> _fileIds, string _cachePath)
        {
            List<DirectoryInfo> assetFolders = new List<DirectoryInfo>();
            if (Directory.Exists(_cachePath))
            {
                DirectoryInfo dir = new DirectoryInfo(_cachePath);
                dir.Refresh();

                var gameDirectories = dir.GetDirectories("*", SearchOption.AllDirectories);

                foreach (var directory in gameDirectories)
                {
                    for (int i = 0; i < _fileIds.Count; i++)
                    {
                        if (directory.Name == _fileIds[i] && !assetFolders.Contains(directory))
                        {
                            assetFolders.Add(directory);
                        }
                    }
                }
            }

            return assetFolders;
        }

        private uint CalculateCRCValue(FileInfo _file)
        {
            var fromFileBytes = File.ReadAllBytes(_file.FullName);
            return Crc32CAlgorithm.Compute(fromFileBytes);
        }

        #endregion

        #region Custom content catalog loading

        public async Task<IResourceLocator> LoadCustomContentCatalog(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                Debug.LogError("[Addressables Downloader] No remotePath when trying to load addressable catalog.");
                return null;
            }

            // Load a catalog from server and automatically release the operation handle.
            var handle = Addressables.LoadContentCatalogAsync(remotePath, false);
            
            await handle.Task;
                
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                onCustomContentCatalogLoaded?.Invoke();
            }
            else if (handle.Status == AsyncOperationStatus.Failed)
            {
                Debug.LogError($"[Addressables Downloader] Loading Custom Content Catalog failed: {remotePath}");
            }
            
            var ret = handle.Result;
            Addressables.Release(handle);

            return ret;
        }

        #endregion
    }
}