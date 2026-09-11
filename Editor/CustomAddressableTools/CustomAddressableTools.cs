// using System;
// using System.Collections.Generic;
// using System.IO;
// using System.Linq;
// using System.Threading.Tasks;
// using UnityEditor.AddressableAssets.Build;
// using UnityEditor.AddressableAssets;
// using UnityEditor;
// using UnityEngine.AddressableAssets.Initialization;
// using UnityEditor.AddressableAssets.Settings;
// using UnityEngine;
// using Force.Crc32;
// using Newtonsoft.Json;
// using Arbelos.BuildUtility.Runtime;
// using static UnityEditor.AddressableAssets.Settings.AddressableAssetProfileSettings;

// namespace Arbelos.BuildUtility.Editor
// {
//     public class CustomAddressableTools
//     {
//         public static async Task BuildAddressables()
//         {
//             // Deletes Old Addressables
//             var path = $"{Directory.GetCurrentDirectory()}/ServerData/{EditorUserBuildSettings.activeBuildTarget.ToString()}";

//             if (!Directory.Exists(path))
//             {
//                 Directory.CreateDirectory(path);
//             }
//             else
//             {
//                 var files = Directory.GetFiles(path);

//                 foreach (var file in files)
//                 {
//                     File.Delete(file);
//                 }

//                 Directory.Delete(path);
//                 Directory.CreateDirectory(path);
                
//                 Debug.Log("<color=orange>Cleared previous addressable files</color>");
//             }
            
//             AddressableAssetSettings.BuildPlayerContent();
            
//             Debug.Log(path);
            
//             await GenerateCRCValues();
//         }
        
//         public static async Task UpdatePreviousAddressablesBuild()
//         {
//             // Deletes Old Addressables
//             var path = $"{Directory.GetCurrentDirectory()}/ServerData/{EditorUserBuildSettings.activeBuildTarget.ToString()}";

//             if (!Directory.Exists(path))
//             {
//                 Directory.CreateDirectory(path);
//             }
//             else
//             {
//                 var files = Directory.GetFiles(path);

//                 foreach (var file in files)
//                 {
//                     File.Delete(file);
//                 }

//                 Directory.Delete(path);
//                 Directory.CreateDirectory(path);
                
//                 Debug.Log("<color=orange>Cleared previous addressable files</color>");
//             }
            
//             var contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
//             if (!string.IsNullOrEmpty(contentStatePath))
//             {
//                 var azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
//                 AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);

//                 Application.logMessageReceived += HandleAddressableUpdateErrors;

//                 ContentUpdateScript.BuildContentUpdate(AddressableAssetSettingsDefaultObject.Settings, contentStatePath);

//                 Debug.Log("<color=orange>Finished updating previous build</color>");
//             }
//             else
//             {
//                 AddressableAssetSettings.BuildPlayerContent();
//                 Debug.Log("<color=orange>No existing state found to update - finish clean addressables build</color>");
//             }
            
//             Application.logMessageReceived -= HandleAddressableUpdateErrors;

//             await GenerateCRCValues();
//         }

//         private static async void HandleAddressableUpdateErrors(string logString, string stackTrace, LogType type)
//         {
//             // Catch errors only
//             if (type == LogType.Error || type == LogType.Exception)
//             {
//                 Debug.Log($"<color=orange>An Error occured while updating previous addressables build: {logString}</color>");
//                 Debug.Log($"<color=orange>Making a clean addressable build instead</color>");
//                 AddressableAssetSettings.BuildPlayerContent();
//                 await GenerateCRCValues();
//             }
//         }

//         private static async void CloudBuild_HandleAddressableUpdateErrors(string logString, string stackTrace, LogType type)
//         {
//             // Catch errors only
//             if (type == LogType.Error || type == LogType.Exception)
//             {
//                 Debug.Log($"<color=orange>An Error occured while updating previous addressables build: {logString}</color>");
//                 Debug.Log($"<color=orange>Making a clean addressable build instead</color>");
//                 AddressableAssetSettings.BuildPlayerContent();
//                 await CloudBuild_GenerateCRCValues();
//             }
//         }

//         public static async Task GenerateCRCValues(string addressablesBuildPath = null)
//         {
//             if (addressablesBuildPath == null)
//             {
//                 string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
//                 addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
//             }

//             if (Directory.Exists(addressablesBuildPath))
//             {
//                 DirectoryInfo dir = new DirectoryInfo(addressablesBuildPath);
//                 GameAddressableData data = Resources.Load<GameAddressableData>("GameAddressableData");
//                 data.AddressableCRCList.Clear();

//                 FileInfo[] files = dir.GetFiles();

//                 for (int i = 0; i < files.Length; i++)
//                 {
//                     var fromFileBytes = await File.ReadAllBytesAsync(files[i].FullName);
//                     data.AddressableCRCList.Add(new AddressableCRCEntry(files[i].Name, Crc32CAlgorithm.Compute(fromFileBytes)));
//                 }
//                 EditorUtility.SetDirty(data);
//                 AssetDatabase.Refresh();
//                 Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated CRC List</color>");
//             }

//         }
        
//         public static void SwitchCurrentAddressablesProfileByName(string profileName)
//        {
//            AddressableAssetSettings assetSettings;
//            AddressableAssetProfileSettings assetProfileSettings;
           
//            //Initialize asset settings and asset profile settings
//            assetSettings = AddressableAssetSettingsDefaultObject.Settings;
//            assetProfileSettings = assetSettings.profileSettings;
           
//            //First Validate if the given named addressable profile exists or not
//            if (!assetProfileSettings.GetAllProfileNames().Contains(profileName))
//            {
//                throw new Exception($"No Addressable Profile exists that goes by name: {profileName}. Please enter the correct name.");
//            }
           
//            assetSettings.activeProfileId = assetProfileSettings.GetProfileId(profileName);
//            assetSettings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
           
//             //Update profile info in game asset file as well.
//            GameAddressableData profileData = Resources.Load<GameAddressableData>("GameAddressableData");
//            profileData.profileName = profileName;
//            profileData.profileId = assetSettings.activeProfileId;
//            EditorUtility.SetDirty(profileData);
//            AssetDatabase.Refresh();
//            Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
//        }
        
//         public static async Task CloudBuild_GenerateCRCValues(string addressablesBuildPath = null)
//         {
//             if (addressablesBuildPath == null)
//             {
//                 string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
//                 addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
//             }

//             if (Directory.Exists(addressablesBuildPath))
//             {
//                 DirectoryInfo dir = new DirectoryInfo(addressablesBuildPath);
//                 GameAddressableData data = (GameAddressableData)AssetDatabase.LoadAssetAtPath("Assets/Resources/GameAddressableData.asset", typeof(GameAddressableData));
//                 data.AddressableCRCList.Clear();

//                 FileInfo[] files = dir.GetFiles();

//                 for (int i = 0; i < files.Length; i++)
//                 {
//                     var fromFileBytes = await File.ReadAllBytesAsync(files[i].FullName);
//                     data.AddressableCRCList.Add(new AddressableCRCEntry(files[i].Name, Crc32CAlgorithm.Compute(fromFileBytes)));
//                 }
//                 EditorUtility.SetDirty(data);
//                 AssetDatabase.Refresh();
//                 Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated CRC List</color>");
//             }
//             else
//             {
//                 Debug.Log($"<color=orange>No addressable build path found: {addressablesBuildPath}</color>");
//             }
//         }
        
//         public static void CloudBuild_SwitchAddressablesProfileByName(string profileName)
//         {
//             AddressableAssetSettings assetSettings;
//             AddressableAssetProfileSettings assetProfileSettings;
           
//             //Initialize asset settings and asset profile settings
//             assetSettings = AddressableAssetSettingsDefaultObject.Settings;
//             assetProfileSettings = assetSettings.profileSettings;
           
//             //First Validate if the given named addressable profile exists or not
//             if (!assetProfileSettings.GetAllProfileNames().Contains(profileName))
//             {
//                 throw new Exception($"No Addressable Profile exists that goes by name: {profileName}. Please enter the correct name.");
//             }
           
//             assetSettings.activeProfileId = assetProfileSettings.GetProfileId(profileName);
//             assetSettings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
           
//             //Update profile info in game asset file as well.
//             GameAddressableData profileData = (GameAddressableData)AssetDatabase.LoadAssetAtPath("Assets/Resources/GameAddressableData.asset", typeof(GameAddressableData));
//             profileData.profileName = profileName;
//             profileData.profileId = assetSettings.activeProfileId;
//             EditorUtility.SetDirty(profileData);
//             AssetDatabase.Refresh();
//             Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
//         }

//         public static async Task CloudBuild_BuildCleanAddressables(string addressablesBuildPath = null)
//         {
//             if (addressablesBuildPath == null)
//             {
//                 string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
//                 addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
//             }

//             if (Directory.Exists(addressablesBuildPath))
//             {
//                 var files = Directory.GetFiles(addressablesBuildPath);

//                 foreach (var file in files)
//                 {
//                     File.Delete(file);
//                 }

//                 Directory.Delete(addressablesBuildPath);
//                 Directory.CreateDirectory(addressablesBuildPath);
                
//                 Debug.Log("<color=orange>Cleared previous addressable files</color>");
                
//                 AddressableAssetSettings.BuildPlayerContent();
            
//                 Debug.Log(addressablesBuildPath);
            
//                 await CloudBuild_GenerateCRCValues(addressablesBuildPath);
//             }
//         }

//         public static async Task CloudBuild_UpdatePreviousAddressablesBuild(string addressablesBuildPath = null)
//         {
//             if (addressablesBuildPath == null)
//             {
//                 string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
//                 addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
//             }

//             if (Directory.Exists(addressablesBuildPath))
//             {
//                 var files = Directory.GetFiles(addressablesBuildPath);

//                 foreach (var file in files)
//                 {
//                     File.Delete(file);
//                 }

//                 Directory.Delete(addressablesBuildPath);
//                 Directory.CreateDirectory(addressablesBuildPath);
                
//                 Debug.Log("<color=orange>Cleared previous addressable files</color>");
                
//                 var contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
//                 if (!string.IsNullOrEmpty(contentStatePath))
//                 {
//                     var azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
//                     AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);

//                     Application.logMessageReceived += CloudBuild_HandleAddressableUpdateErrors;

//                     ContentUpdateScript.BuildContentUpdate(AddressableAssetSettingsDefaultObject.Settings, contentStatePath);

//                     Debug.Log("<color=orange>Finished updating previous build</color>");
//                 }
//                 else
//                 {
//                     AddressableAssetSettings.BuildPlayerContent();
//                     Debug.Log("<color=orange>No existing state found to update - finish clean addressables build</color>");
//                 }
            
//                 Application.logMessageReceived -= CloudBuild_HandleAddressableUpdateErrors;

//                 await CloudBuild_GenerateCRCValues(addressablesBuildPath);
//             }
//         }
//     }
// }
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.AddressableAssets.Initialization;
using Force.Crc32;
using Arbelos.BuildUtility.Runtime;

namespace Arbelos.BuildUtility.Editor
{
    public class CustomAddressableTools
    {
        // Task signatures are retained for existing callers. All work completes
        // synchronously on the Unity editor thread; do not run these on Task.Run.
        public static Task BuildAddressables()
        {
            string path = ResolveBuildPath(null);
            PrepareOutputDirectory(path);
            BuildPlayerContentOrThrow();
            return GenerateCRCValues(path);
        }

        public static Task UpdatePreviousAddressablesBuild()
        {
            string path = ResolveBuildPath(null);
            BuildUpdateOrNewContent(path);
            return GenerateCRCValues(path);
        }

        public static void BuildPlayerContentOrThrow()
        {
            AddressablesPlayerBuildResult result;
            AddressableAssetSettings.BuildPlayerContent(out result);
            ValidateBuildResult(result);
        }

        private static void ValidateBuildResult(AddressablesPlayerBuildResult result)
        {
            if (result == null)
                throw new BuildFailedException("Addressables build returned no result.");

            if (!string.IsNullOrEmpty(result.Error))
                throw new BuildFailedException("Addressables build failed: " + result.Error);
        }

        private static string ResolveBuildPath(string path)
        {
            if (path == null)
                path = Path.Combine(Application.dataPath, "..", "ServerData",
                    EditorUserBuildSettings.activeBuildTarget.ToString());

            if (string.IsNullOrWhiteSpace(path))
                throw new BuildFailedException("Addressables output path is empty.");

            return Path.GetFullPath(path);
        }

        private static void PrepareOutputDirectory(string path)
        {
            // Preserve the original flat-output contract. Reject nested output
            // before deleting files rather than silently dropping subdirectories.
            if (Directory.Exists(path) && Directory.GetDirectories(path).Length > 0)
                throw new BuildFailedException(
                    "Expected a flat Addressables output directory, but found subdirectories: " + path);

            Directory.CreateDirectory(path);
            foreach (string file in Directory.GetFiles(path))
                File.Delete(file);

            Debug.Log("Cleared previous Addressables files: " + path);
        }

        private static void BuildUpdateOrNewContent(string path)
        {
            string contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
            // Validate an explicitly returned state path before clearing output.
            if (!string.IsNullOrEmpty(contentStatePath))
            {
                if (!File.Exists(contentStatePath))
                    throw new BuildFailedException("Addressables content state file is missing: " + contentStatePath);

                string statePath = Path.GetFullPath(contentStatePath);
                string outputPrefix = path.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (statePath.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException(
                        "Content state file must be outside the output directory being cleared: " + statePath);
            }

            PrepareOutputDirectory(path);
            if (string.IsNullOrEmpty(contentStatePath))
            {
                BuildPlayerContentOrThrow();
                Debug.Log("No content state selected; completed a new Addressables build.");
                return;
            }

            string azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
            AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);
            AddressablesPlayerBuildResult result = ContentUpdateScript.BuildContentUpdate(
                AddressableAssetSettingsDefaultObject.Settings, contentStatePath);
            ValidateBuildResult(result);
            Debug.Log("Finished updating previous Addressables build.");
        }

        public static Task GenerateCRCValues(string addressablesBuildPath = null)
        {
            GameAddressableData data = Resources.Load<GameAddressableData>("GameAddressableData");
            GenerateCRCValuesAndSave(ResolveBuildPath(addressablesBuildPath), data);
            return Task.CompletedTask;
        }

        public static Task CloudBuild_GenerateCRCValues(string addressablesBuildPath = null)
        {
            GameAddressableData data = AssetDatabase.LoadAssetAtPath<GameAddressableData>(
                "Assets/Resources/GameAddressableData.asset");
            GenerateCRCValuesAndSave(ResolveBuildPath(addressablesBuildPath), data);
            return Task.CompletedTask;
        }

        private static void GenerateCRCValuesAndSave(string path, GameAddressableData data)
        {
            if (data == null)
                throw new BuildFailedException("GameAddressableData asset was not found.");
            if (data.AddressableCRCList == null)
                throw new BuildFailedException("GameAddressableData.AddressableCRCList is null.");
            if (!Directory.Exists(path))
                throw new BuildFailedException("Addressables output directory was not found: " + path);
            if (Directory.GetDirectories(path).Length > 0)
                throw new BuildFailedException("CRC generation expects flat Addressables output: " + path);

            FileInfo[] files = new DirectoryInfo(path).GetFiles()
                .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
            if (files.Length == 0)
                throw new BuildFailedException("Addressables output directory is empty: " + path);

            // Calculate everything first so a read failure does not leave a partial list.
            var entries = new List<AddressableCRCEntry>();
            foreach (FileInfo file in files)
            {
                byte[] bytes = File.ReadAllBytes(file.FullName);
                entries.Add(new AddressableCRCEntry(file.Name, Crc32CAlgorithm.Compute(bytes)));
            }

            data.AddressableCRCList.Clear();
            foreach (AddressableCRCEntry entry in entries)
                data.AddressableCRCList.Add(entry);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Debug.Log("Saved Addressable Game Data with updated CRC list.");
        }

        public static void SwitchCurrentAddressablesProfileByName(string profileName)
        {
            SwitchProfile(profileName, Resources.Load<GameAddressableData>("GameAddressableData"));
        }

        public static void CloudBuild_SwitchAddressablesProfileByName(string profileName)
        {
            SwitchProfile(profileName, AssetDatabase.LoadAssetAtPath<GameAddressableData>(
                "Assets/Resources/GameAddressableData.asset"));
        }

        private static void SwitchProfile(string profileName, GameAddressableData data)
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || settings.profileSettings == null)
                throw new BuildFailedException("Addressables settings were not found.");
            if (data == null)
                throw new BuildFailedException("GameAddressableData asset was not found.");
            if (!settings.profileSettings.GetAllProfileNames().Contains(profileName))
                throw new BuildFailedException("No Addressables profile exists named: " + profileName);

            settings.activeProfileId = settings.profileSettings.GetProfileId(profileName);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
            data.profileName = profileName;
            data.profileId = settings.activeProfileId;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Debug.Log("Saved Addressables profile: " + profileName);
        }

        public static Task CloudBuild_BuildCleanAddressables(string addressablesBuildPath = null)
        {
            string path = ResolveBuildPath(addressablesBuildPath);
            PrepareOutputDirectory(path);
            BuildPlayerContentOrThrow();
            return CloudBuild_GenerateCRCValues(path);
        }

        public static Task CloudBuild_UpdatePreviousAddressablesBuild(string addressablesBuildPath = null)
        {
            string path = ResolveBuildPath(addressablesBuildPath);
            BuildUpdateOrNewContent(path);
            return CloudBuild_GenerateCRCValues(path);
        }
    }
}
