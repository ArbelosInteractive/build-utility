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
using Newtonsoft.Json;
using System;
using System.Collections;

namespace Arbelos.BuildUtility.Runtime
{
    public class AddressablesDownloader : MonoBehaviour
    {
        #region public variables

        [HideInInspector] public bool isInitialized;
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
        private bool wasConnected;
        private bool wasPaused;
        private bool hadFocus;
        //Used to track that initial addressables initialization code has been run.
        private bool addressablesInitialized;
        
        #endregion
        
        void OnApplicationPause(bool pauseStatus)
        {
            if (isInitialized || !addressablesInitialized)
                return;

            if (pauseStatus != wasPaused)
            {
                if (pauseStatus)
                {
                    Debug.Log("App paused (device may be sleeping or switching apps)");
                }
                else
                {
                    Debug.Log("App resumed");
                    ResumePendingDownload();
                }
                wasPaused = pauseStatus;
            }
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (isInitialized || !addressablesInitialized)
                return;

            if (hasFocus != hadFocus)
            {
                if (!hasFocus)
                {
                    Debug.Log("App lost focus (possibly screen off or user switched apps)");
                }
                else
                {
                    Debug.Log("App regained focus");
                    ResumePendingDownload();
                }
                hadFocus = hasFocus;
            }
        }
        
        IEnumerator CheckInternet()
        {
            while (!isInitialized)
            {
                if (!addressablesInitialized)
                {
                    yield return new WaitForSeconds(5f); // check every 5 seconds
                }
                
                bool isConnected = Application.internetReachability != NetworkReachability.NotReachable;
                if (isConnected != wasConnected)
                {
                    if (isConnected)
                    {                    
                        Debug.Log("Internet connected");
                        ResumePendingDownload();    
                    }
                    else
                    {
                        Debug.Log("Internet disconnected");
                    }

                    wasConnected = isConnected;
                }
                
                yield return new WaitForSeconds(5f); // check every 5 seconds
            }
        }

        protected static float BytesToKiloBytes(long bytes)
        {
            return bytes / 1024f;
        }

        protected static void ClearPreviousCatalog()
        {
            string path = Application.persistentDataPath + "/com.unity.addressables/";
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);
                //Refresh the directory before checking again.
                dir.Refresh();

                //first the hash files
                FileInfo[] files = dir.GetFiles("catalog*.hash").OrderByDescending(p => p.LastWriteTime).ToArray();

                if (files.Length > 1)
                {
                    //skip the first file we want to keep it
                    for (int i = 1; i < files.Length; i++)
                    {
                        FileInfo file = files[i];
                        Debug.Log($"deleted: {file.Name}");
                        file.Delete();
                    }
                }

                //now the json files
                FileInfo[] jsonfiles = dir.GetFiles("catalog*.json").OrderByDescending(p => p.LastWriteTime).ToArray();

                if (jsonfiles.Length > 1)
                {
                    //skip the first file we want to keep it
                    for (int i = 1; i < jsonfiles.Length; i++)
                    {
                        FileInfo file = jsonfiles[i];
                        Debug.Log($"deleted: {file.Name}");
                        file.Delete();
                    }
                }
            }
        }

        public async Task UpdateAndDownload()
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
                PurgeAddressableFiles();
                onUpdateAvailable?.Invoke();
                var allKeys = updatedResourceLocators[0].Keys;

                for (int i = 1; i < updatedResourceLocators.Count; i++)
                {
                    allKeys.Append(updatedResourceLocators[i].Keys);
                }

                pendingKeys = allKeys.ToList();

                await DownloadKeysAsync(pendingKeys);
            }
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

            // Refresh Directories before doing anything
            RefreshCacheAndCatalogDirectories();

            ClearPreviousCatalog();

            AsyncOperationHandle<IResourceLocator> handle = Addressables.InitializeAsync(false);

            await handle.Task;

            addressablesInitialized = true;

            Addressables.Release(handle);
#if !UNITY_EDITOR
            addressableData = Resources.Load<GameAddressableData>("GameAddressableData");
            if (addressableData != null && !String.IsNullOrEmpty(addressableData.profileName))
            {
                var profileType = addressableData.profileName;
                if (profileType != "EditorHosted")
                {
                    AsyncOperationHandle<List<string>> catalogHandle = Addressables.CheckForCatalogUpdates(false);

                    await catalogHandle.Task;

                    List<string> possibleUpdates = catalogHandle.Result;

                    Addressables.Release(catalogHandle);

                    if (possibleUpdates.Count > 0)
                    {
                        Debug.Log("Update available");
                        await UpdateAndDownload();
                    }
                    else
                    {
                        Debug.Log("No update available");
                    }

                    //validate files
                    if (ValidateCurrentlyDownloadedFiles())
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
                PurgeAddressableFiles();
                onValidationFail?.Invoke();
                StartReDownload();
                Debug.Log($"<color=orange>INVALID CATALOG FILES DETECTED!!</color>");
                return false;
            }

            List<string> cachePaths = new List<string>();
            Caching.GetAllCachePaths(cachePaths);

            string cachePath = cachePaths[0];

            //File IDS will be used to search for folders in cachePath assets that are under the same folder names as the assetFileIDs
            List<string> assetsFileIds = FetchGameAssetsFileIds(addressableData.AddressableCRCList);

            if (!ValidateGameFiles(addressableData.AddressableCRCList, assetsFileIds, cachePath))
            {
                PurgeAddressableFiles();
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

            //List<IResourceLocator> updatedResourceLocators = await Addressables.UpdateCatalogs(true);

            if (existingLocators != null)
            {
                var allKeys = existingLocators[0].Keys;

                for (int i = 1; i < existingLocators.Count; i++)
                {
                    allKeys.Append(existingLocators[i].Keys);
                }
                
                pendingKeys = allKeys.ToList();

                await DownloadKeysAsync(pendingKeys);

                //validate files
                if (ValidateCurrentlyDownloadedFiles())
                {
                    isInitialized = true;
                    onInitialized?.Invoke();
                }
            }
        }

        private async void ResumePendingDownload()
        {
            await DownloadKeysAsync(pendingKeys);

            //validate files
            if (ValidateCurrentlyDownloadedFiles())
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

        private async Task DownloadKeysAsync(List<object> _keys)
        {
            try
            {
                numAssetBundlesToDownload = pendingKeys.Count + downloadedKeys.Count;
                numDownloaded = downloadedKeys.Count;

                //Use a separate list to iterate through, so we don't modify the same one inside the loop
                foreach (var key in _keys.ToArray())
                {
                    numDownloaded++;

                    //if (ui != null)
                    //{
                    //    ui.UpdateDownloadsText($"downloading ... {numDownloaded}/{numAssetBundlesToDownload}");
                    //}

                    var keyDownloadSizeKb = BytesToKiloBytes(await Addressables.GetDownloadSizeAsync(key).Task);
                    if (keyDownloadSizeKb <= 0)
                    {
                        continue;
                    }

                    var keyDownloadOperation = Addressables.DownloadDependenciesAsync(key);
                    while (!keyDownloadOperation.IsDone)
                    {
                        await Task.Yield();
                        var status = keyDownloadOperation.GetDownloadStatus();
                        percentageDownloaded = status.Percent * 100.0f;
                        onPercentageDownloaded.Invoke(percentageDownloaded);
                        //if (ui != null)
                        //{
                        //    ui.UpdateProgressText(percent);
                        //}
                    }

                    if (keyDownloadOperation.IsDone)
                    {
                        //Update pending and downloaded keys list
                        pendingKeys.Remove(key);
                        downloadedKeys.Add(key);

                        //Release the operation handle once a key has been downloaded
                        Addressables.Release(keyDownloadOperation);
                    }
                }

                Debug.Log("all downloads completed");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Addressables Downloader] Error - {ex.Message}");
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
                                return false;
                            }
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
            return isValid;
        }

        private bool ValidateGameFiles(List<AddressableCRCEntry> _data, List<string> _fileIds, string _cachePath)
        {
            bool isValid = false;

            List<DirectoryInfo> assetFolders = FindAssetFolders(_fileIds, _cachePath);

            if (assetFolders.Count > 0)
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

        private async void PurgeAddressableFiles()
        {
            //Addressables.ClearDependencyCacheAsync(Addressables.ResourceLocators.FirstOrDefault().LocatorId);
            //Addressables.ClearResourceLocators();

            bool cacheCleared = Caching.ClearCache();
            //PurgeCatalogFiles();
        }

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
        
        public async void LoadCustomContentCatalog(string remotePath)
        {
            if (string.IsNullOrEmpty(remotePath))
            {
                Debug.LogError("No remotePath when trying to load addressable");
                return;
            }

            //Load a catalog from sever and automatically release the operation handle.
            AsyncOperationHandle <IResourceLocator> handle
                = Addressables.LoadContentCatalogAsync(remotePath, false);
            
            await handle.Task;
                
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                onCustomContentCatalogLoaded?.Invoke();
            }
            else if (handle.Status == AsyncOperationStatus.Failed)
            {
                Debug.LogError($"Loading Custom Content Catalog failed: {remotePath}");
            }
                
            Addressables.Release(handle);
        }
    }
}