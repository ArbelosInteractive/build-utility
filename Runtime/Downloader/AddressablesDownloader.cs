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
        public UnityEvent onValidationFail;
        public UnityEvent onCustomContentCatalogLoaded;
        public UnityEvent<float> onPercentageDownloaded;
        public GameAddressableData addressableData;

        private List<object> downloadedKeys = new();
        private List<object> pendingKeys = new();
        private bool wasConnected = true;
        private bool wasPaused;
        private bool hasError = false;
        
        //Used to track that initial addressables initialization code has been run.
        private bool addressablesInitialized;
        private bool downloadInitialized;
        private AsyncOperationHandle downloadHandle;
        private AsyncOperationHandle clearHandle;
        private AsyncOperationHandle<long> downloadSizeHandle;

        private bool assetDownloadActive = false;
        private CancellationTokenSource downloadCancellationTokenSource;
        
        #endregion
        
        void OnApplicationPause(bool pauseStatus)
        {
            if (isInitialized || !addressablesInitialized)
                return;

            if (pauseStatus != wasPaused && wasConnected)
            {
                wasPaused = pauseStatus;
                if (pauseStatus)
                {
                    Debug.Log("App paused (device may be sleeping or switching apps)");
                    CancelDownload();

                }
                else
                {
                    Debug.Log("App resumed");
                    ResumePendingDownload();
                }
            }
        }
        
        IEnumerator CheckInternet()
        {
            while (!isInitialized)
            {
                //Waits for addressables and first download to initialize
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
                        Debug.Log("Internet connected");
                        ResumePendingDownload();    
                    }
                    else
                    {
                        Debug.Log("Internet disconnected");
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

        protected void ClearPreviousCatalog()
        {
            if (addressableData == null)
            {
                Debug.LogError("[Addressables Downloader] No Addressables Data was found");
                return;
            }

            var dataCatalogHash = addressableData.AddressableCRCList.Find(x => x.key.Contains(".hash"));
            var dataCatalogJson = addressableData.AddressableCRCList.Find(x => x.key.Contains(".json"));
            
#if UNITY_6000_0_OR_NEWER
            if(dataCatalogJson == null)
            {
                dataCatalogJson = addressableData.AddressableCRCList.Find(x => x.key.Contains(".bin"));
            }
#endif
           
            
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);
                //Refresh the directory before checking again.
                dir.Refresh();

                //first get all the hash files
                FileInfo[] allHashFiles = dir.GetFiles("catalog*.hash").OrderByDescending(p => p.LastWriteTime).ToArray();
                
                //Get the current addressable data hash file
                string hashFilePath = Path.Combine(dir.FullName, dataCatalogHash.key);
                FileInfo currentHashFile = null;
                if (File.Exists(hashFilePath))
                {
                    currentHashFile = new FileInfo(hashFilePath);
                }
                else
                {
                    Debug.Log($"[Addressables Downloader] Current Catalog Hash File not found: {hashFilePath}, new will be created.");
                }
                if (allHashFiles.Length > 1)
                {
                    //delete every other file except the current addressable data catalog hash file
                    for (int i = 0; i < allHashFiles.Length; i++)
                    {
                        if (currentHashFile != null && allHashFiles[i] == currentHashFile) continue;
                        FileInfo file = allHashFiles[i];
                        
                        //Don't delete the one we need
                        if (file.Name.Equals(dataCatalogHash.key)) continue;
                        
                        Debug.Log($"[Addressables Downloader] Catalog Hash File Deleted: {file.Name}");
                        file.Delete();
                    }
                }
                else
                {
                    if(allHashFiles.Length == 1)
                        Debug.Log("[AddressablesDownloader] No Previous Catalog Hash file found. Only one exists.");
                    else
                        Debug.LogError("[AddressablesDownloader] No Catalog Hash files found. Zero exists.");
                }

                //now get all the json files
                FileInfo[] allJsonFiles = dir.GetFiles("catalog*.json").OrderByDescending(p => p.LastWriteTime).ToArray();
                
#if UNITY_6000_0_OR_NEWER
                if(allJsonFiles.Length == 0)
                {
                    allJsonFiles = dir.GetFiles("catalog*.bin").OrderByDescending(p => p.LastWriteTime).ToArray();
                }
#endif
                
                //Get the current addressable data json file
                string jsonFilePath = Path.Combine(dir.FullName, dataCatalogJson.key);
                FileInfo currentJsonFile = null;
                if (File.Exists(jsonFilePath))
                {
                    currentJsonFile = new FileInfo(jsonFilePath);
                }
                else
                {
                    Debug.Log($"[Addressables Downloader] Current Catalog Json File not found: {jsonFilePath}, new will be created.");
                }

                if (allJsonFiles.Length > 1)
                {
                    //delete every other file except the current addressable data catalog json file
                    for (int i = 0; i < allJsonFiles.Length; i++)
                    {
                        if (currentJsonFile != null && allJsonFiles[i] == currentJsonFile) continue;
                        
                        FileInfo file = allJsonFiles[i];
                        
                        //Don't delete the one we need
                        if (file.Name.Equals(dataCatalogJson.key)) continue;
                        
                        Debug.Log($"[Addressables Downloader] Catalog Json File Deleted: {file.Name}");
                        file.Delete();
                    }
                }
                else
                {
                    if(allJsonFiles.Length == 1)
                        Debug.Log("[AddressablesDownloader] No Previous Catalog Json file found. Only one exists.");
                    else
                        Debug.LogError("[AddressablesDownloader] No Catalog Json files found. Zero exists.");
                }
            }
            else
            {
                Debug.LogError("[Addressables Downloader] No catalog cache directory found!");
            }
        }

        public async Task<bool> UpdateAndDownload()
        {
            AsyncOperationHandle<List<IResourceLocator>> handle = Addressables.UpdateCatalogs(true, null, false);

            await handle.Task;

            List<IResourceLocator> updatedResourceLocators = handle.Result;

            Addressables.Release(handle);
            
            pendingKeys.Clear();
            downloadedKeys.Clear();

            if (updatedResourceLocators != null)
            {
                //Clears old files before downloading new ones
                //PurgeAddressableFiles();
                onUpdateAvailable?.Invoke();
                var allKeys = updatedResourceLocators[0].Keys;

                for (int i = 1; i < updatedResourceLocators.Count; i++)
                {
                    allKeys.Append(updatedResourceLocators[i].Keys);
                }

                pendingKeys = allKeys.ToList();
                
                CancelDownload();

                // Await a coroutine using TaskCompletionSource
                var tcs = new TaskCompletionSource<bool>();
                DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

                return await tcs.Task;
            }

            return true;
        }

        public async void Initialize()
        {
            wasConnected = Application.internetReachability != NetworkReachability.NotReachable;
            StartCoroutine(CheckInternet());
            
            //wait for caching to get ready
            while (!Caching.ready)
            {
                await Task.Delay(1000);
            }
            
#if !UNITY_EDITOR
            addressableData = Resources.Load<GameAddressableData>("GameAddressableData");
#endif
            // Refresh Directories before doing anything
            RefreshCacheAndCatalogDirectories();

            ClearPreviousCatalog();

            AsyncOperationHandle<IResourceLocator> handle = Addressables.InitializeAsync(false);

            await handle.Task;

            addressablesInitialized = true;

            Addressables.Release(handle);
#if !UNITY_EDITOR
            if (addressableData != null && !String.IsNullOrEmpty(addressableData.profileName))
            {
                var profileType = addressableData.profileName;
                if (profileType != "EditorHosted")
                {
                    AsyncOperationHandle<List<string>> catalogHandle = Addressables.CheckForCatalogUpdates(false);

                    await catalogHandle.Task;

                    List<string> possibleUpdates = catalogHandle.Result;

                    Addressables.Release(catalogHandle);

                    bool downloadDone = false;
                    if (possibleUpdates.Count > 0)
                    {
                        Debug.Log("Update available");
                        downloadDone = await UpdateAndDownload();
                    }
                    else
                    {
                        Debug.Log("No update available");
                        downloadDone = true;
                    }

                    downloadInitialized = true;

                    //validate files
                    if (downloadDone && ValidateCurrentlyDownloadedFiles())
                    {
                        isInitialized = true;
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
                Debug.LogError("Addressable Profile Data not found, please build correctly!");
            }
#endif
#if UNITY_EDITOR
                isInitialized = true;
                onInitialized?.Invoke();
#endif
        }

        private bool ValidateCurrentlyDownloadedFiles()
        {
#if !UNITY_EDITOR
            //Fetch stored CRC Data when addressables built.
            if (!ValidateCatalogFiles(addressableData.AddressableCRCList))
            {
                //PurgeAddressableFiles();
                onValidationFail?.Invoke();
                StartReDownload();
                Debug.Log($"<color=orange>INVALID CATALOG FILES DETECTED!!</color>");
                return false;
            }

            List<string> cachePaths = new List<string>();
            Caching.GetAllCachePaths(cachePaths);

            string cachePath = cachePaths[0];

            //File IDS will be used to search for folders in cachePath assets that are under the same folder names as the assetFileIDs
           List< string> assetsFileIds = FetchGameAssetsFileIds(addressableData.AddressableCRCList);

            if (!ValidateGameFiles(addressableData.AddressableCRCList, assetsFileIds, cachePath))
            {
                //PurgeAddressableFiles();
                onValidationFail?.Invoke();
                StartReDownload();
                Debug.Log($"<color=orange>INVALID GAME FILES DETECTED!!</color>");
                return false;
            }
#endif
            return true;
        }

        private async void RedownloadGameFiles()
        {
            // Refresh Directories before doing anything
            //RefreshCacheAndCatalogDirectories();

            //ClearPreviousCatalog();
            var existingLocators = Addressables.ResourceLocators.ToList();

            pendingKeys.Clear();
            downloadedKeys.Clear();
            

            if (existingLocators != null)
            {
                var allKeys = existingLocators[0].Keys;

                for (int i = 1; i < existingLocators.Count; i++)
                {
                    allKeys.Append(existingLocators[i].Keys);
                }
                
                pendingKeys = allKeys.ToList();
                
                CancelDownload();

                // Await a coroutine using TaskCompletionSource
                var tcs = new TaskCompletionSource<bool>();
                DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

                var downloadDone = await tcs.Task;

                //validate files
                if (downloadDone && ValidateCurrentlyDownloadedFiles())
                {
                    isInitialized = true;
                    onInitialized?.Invoke();
                }
            }
        }

        private async void ResumePendingDownload()
        {
            Debug.Log($"[Addressables Downloader] Resuming Download...");
            
            //Remove any stragglers already existing in downloaded keys.
            pendingKeys = pendingKeys.Distinct().Except(downloadedKeys).ToList();
            
            CancelDownload();

            // Await a coroutine using TaskCompletionSource
            var tcs = new TaskCompletionSource<bool>();
            DownloadKeysAsync(pendingKeys, success => { tcs.SetResult(success); });

            var downloadDone = await tcs.Task;

            //validate files
            if (downloadDone && ValidateCurrentlyDownloadedFiles())
            {
                isInitialized = true;
                onInitialized?.Invoke();
            }
        }

        private void StartReDownload()
        {
            //Initialize the download again if corrupted files!
            //Initialize();
            RedownloadGameFiles();
        }
        
        // Call this to cancel the download
        public void CancelDownload()
        {
            if (downloadCancellationTokenSource != null && !downloadCancellationTokenSource.IsCancellationRequested)
            {
                downloadCancellationTokenSource.Cancel();
                Debug.Log("[Addressables Downloader] Cancellation requested.");
            }
            StopDownloadHandles();
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

            numAssetBundlesToDownload = pendingKeys.Count + downloadedKeys.Count;
            numDownloaded = downloadedKeys.Count;

            try
            {
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
                    
                    //Skip download if selected to be skipped.
                    if (assetsToSkip.Exists(x => x.Contains(key.ToString())))
                    {
                        Debug.Log($"[Addressables Downloader] {key} Key was skipped as requested.");
                        
                        if (!pendingKeys.Remove(key))
                            Debug.Log($"[Addressables Downloader] {key} Key was not removed from Pending Keys");

                        downloadedKeys.Add(key);

                        numDownloaded++;
                        percentageDownloaded = ((float)numDownloaded / numAssetBundlesToDownload) * 100f;
                        onPercentageDownloaded?.Invoke(percentageDownloaded);
                        continue;
                    }

                    AsyncOperationHandle<long> downloadSizeHandle = default;
                    if (!key.ToString().Contains("unitybuiltinshaders"))
                    {
                        downloadSizeHandle = Addressables.GetDownloadSizeAsync(key);
                        await downloadSizeHandle.Task;

                        var keyDownloadSizeKb = BytesToKiloBytes(downloadSizeHandle.Result);
                        if (keyDownloadSizeKb <= 0)
                        {
                            if (!pendingKeys.Remove(key))
                                Debug.Log($"[Addressables Downloader] {key} Key was not removed from Pending Keys");

                            downloadedKeys.Add(key);
                            Addressables.Release(downloadSizeHandle);

                            numDownloaded++;
                            percentageDownloaded = ((float)numDownloaded / numAssetBundlesToDownload) * 100f;
                            onPercentageDownloaded?.Invoke(percentageDownloaded);
                            continue;
                        }

                        Addressables.Release(downloadSizeHandle);
                    }

                    AsyncOperationHandle clearHandle = Addressables.ClearDependencyCacheAsync(key, false);
                    await clearHandle.Task;
                    if (clearHandle.Status == AsyncOperationStatus.Succeeded)
                    {
                        Debug.Log($"[Addressables Downloader] Cleared cache for key: {key}");
                    }
                    else
                    {
                        Debug.LogWarning($"[Addressables Downloader] Failed to clear cache for key: {key}. Will still attempt download.");
                    }
                    Addressables.Release(clearHandle);

                    AsyncOperationHandle downloadHandle = Addressables.DownloadDependenciesAsync(key);
                    await downloadHandle.Task;

                    if (downloadHandle.Status == AsyncOperationStatus.Succeeded)
                    {
                        if (!pendingKeys.Remove(key))
                            Debug.Log($"[Addressables Downloader] {key} Key was not removed from Pending Keys");
                        downloadedKeys.Add(key);
                    }
                    else
                    {
                        hasError = true;
                        Debug.LogError($"Download failed for key: {key}");
                        Addressables.Release(downloadHandle);
                        continue;
                    }

                    Addressables.Release(downloadHandle);

                    numDownloaded++;
                    percentageDownloaded = ((float)numDownloaded / numAssetBundlesToDownload) * 100f;
                    onPercentageDownloaded?.Invoke(percentageDownloaded);
                }

                if (!hasError && !wasPaused && wasConnected)
                {
                    Debug.Log("All downloads completed successfully.");
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
        
        private void RefreshCacheAndCatalogDirectories()
        {
            //Refresh the catalog directory.
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);

                dir.Refresh();
            }

            //Refresh Cache Directory
            List<string> cachePaths = new List<string>();
            Caching.GetAllCachePaths(cachePaths);
            string cachePath = cachePaths[0];
            if (Directory.Exists(cachePath))
            {
                DirectoryInfo dir = new DirectoryInfo(cachePath);
                dir.Refresh();
            }
        }

        private void StopDownloadHandles()
        {
            if (downloadHandle.IsValid())
            {
                Addressables.Release(downloadHandle);
                Debug.Log("[Addressables Downloader] Download Handle Released");
            }
            if (clearHandle.IsValid())
            {
                Addressables.Release(clearHandle);
                Debug.Log("[Addressables Downloader] Clear Handle Released");
            }
            if (downloadSizeHandle.IsValid())
            {
                Addressables.Release(downloadSizeHandle);
                Debug.Log("[Addressables Downloader] Download Size Handle Released");
            }
        }

        private List<string> FetchGameAssetsFileIds(List<AddressableCRCEntry> _data)
        {
            List<string> _assetsFileIds = new List<string>();

            foreach (var data in _data)
            {
                //Unity Built In Shader bundle file
                if (data.key.Contains("unitybuiltinshaders"))
                {
                    string fileName = data.key;
                    string[] parts = fileName.Split('_');
                    if (parts.Length >= 3)
                    {
                        var finalName = parts[0] + "_unitybuiltinshaders";
                        _assetsFileIds.Add(finalName);
                    }
                }
                else
                {
                    // Game/Scene Bundle Files
                    string fileName = data.key;
                    string[] parts = fileName.Split('_');
                    if (parts.Length >= 3)
                    {
                        string[] subParts = parts[3].Split('.'); //Separate the .bundle from the file name.
                        if (subParts.Length > 0)
                        {
                            string targetString = subParts[0];  //get the file id
                                                                // Use targetString as needed
                            _assetsFileIds.Add(targetString); // Output: f59db7a2af3be597e715cca63b051863
                        }
                    }
                }
            }
            return _assetsFileIds;
        }
        
        private bool ValidateCatalogFiles(List<AddressableCRCEntry> _data)
        {
            ClearPreviousCatalog();
            bool isValid = false;
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);
                //Refresh the directory before checking again.
                dir.Refresh();
                //first the hash files
                FileInfo[] files = dir.GetFiles("catalog*.hash");
                //now the json files
                FileInfo[] jsonfiles = dir.GetFiles("catalog*.json");
                
#if UNITY_6000_0_OR_NEWER
                if(jsonfiles.Length == 0)
                {
                    jsonfiles = dir.GetFiles("catalog*.bin");
                }
#endif

                if (files.Length > 0 && jsonfiles.Length > 0)
                {
                    uint hashFileValue = CalculateCRCValue(files[0]);
                    uint jsonFileValue = CalculateCRCValue(jsonfiles[0]);

                    foreach (var data in _data)
                    {
                        if (data.key.Contains(".hash"))
                        {
                            if (data.value == hashFileValue)
                            {
                                isValid = true;
                            }
                            else
                            {
                                Debug.LogError($"[Addressables Downloader] Catalog File: {data.key} is invalid.");
                                return false;
                            }
                        }
                        if (data.key.Contains(".json"))
                        {
                            if (data.value == jsonFileValue)
                            {
                                isValid = true;
                            }
                            else
                            {
                                Debug.LogError($"[Addressables Downloader] Catalog File: {data.key} is invalid.");
                                return false;
                            }
                        }
#if UNITY_6000_0_OR_NEWER
                        else if (data.key.Contains(".bin"))
                        {
                            if (data.value == jsonFileValue)
                            {
                                isValid = true;
                            }
                            else
                            {
                                Debug.LogError($"[Addressables Downloader] Catalog File: {data.key} is invalid.");
                                return false;
                            }
                        }
#endif
                    }
                }
                else
                {
                    Debug.LogError("[Addressables Downloader] No catalog JSON and HASH Files Found");
                    return false;
                }
            }
            else
            {
                Debug.LogError("[Addressables Downloader] No catalog cache directory found!");
            }
            return isValid;
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
                            //Check if a sub folder exists
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
                //Refresh the directory before checking again.
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

        // private async void PurgeAddressableFiles()
        // {
        //     //Addressables.ClearDependencyCacheAsync(Addressables.ResourceLocators.FirstOrDefault().LocatorId);
        //     //Addressables.ClearResourceLocators();
        //
        //     //bool cacheCleared = Caching.ClearCache();
        //     //PurgeCatalogFiles();
        // }

        private void PurgeCatalogFiles()
        {
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);

                //first the hash files
                FileInfo[] files = dir.GetFiles("catalog*.hash").OrderByDescending(p => p.LastWriteTime).ToArray();

                if (files.Length > 0)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        FileInfo file = files[i];
                        file.Delete();
                    }
                }

                //now the json files
                FileInfo[] jsonfiles = dir.GetFiles("catalog*.json").OrderByDescending(p => p.LastWriteTime).ToArray();

#if UNITY_6000_0_OR_NEWER
                if(jsonfiles.Length == 0)
                {
                    jsonfiles = dir.GetFiles("catalog*.bin").OrderByDescending(p => p.LastWriteTime).ToArray();
                }
#endif
                
                if (jsonfiles.Length > 0)
                {
                    for (int i = 0; i < jsonfiles.Length; i++)
                    {
                        FileInfo file = jsonfiles[i];
                        file.Delete();
                    }
                }
            }
        }
        
        public async Task<IResourceLocator> LoadCustomContentCatalog(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                Debug.LogError("No remotePath when trying to load addressable");
                return null;
            }

            //Load a catalog from sever and automatically release the operation handle.
            var handle = Addressables.LoadContentCatalogAsync(remotePath, false);
            
            await handle.Task;
                
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                onCustomContentCatalogLoaded?.Invoke();
            }
            else if (handle.Status == AsyncOperationStatus.Failed)
            {
                Debug.LogError($"Loading Custom Content Catalog failed: {remotePath}");
            }
            
            var ret = handle.Result;
            Addressables.Release(handle);

            return ret;
        }
    }
}