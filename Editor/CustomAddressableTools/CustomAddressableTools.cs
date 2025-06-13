using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets;
using UnityEditor;
using UnityEngine.AddressableAssets.Initialization;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using Force.Crc32;
using Newtonsoft.Json;
using Arbelos.BuildUtility.Runtime;
using static UnityEditor.AddressableAssets.Settings.AddressableAssetProfileSettings;

namespace Arbelos.BuildUtility.Editor
{
    public class CustomAddressableTools
    {
        public static async Task BuildAddressables()
        {
            // Deletes Old Addressables
            var path = $"{Directory.GetCurrentDirectory()}/ServerData/{EditorUserBuildSettings.activeBuildTarget.ToString()}";

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                var files = Directory.GetFiles(path);

                foreach (var file in files)
                {
                    File.Delete(file);
                }

                Directory.Delete(path);
                Directory.CreateDirectory(path);
                
                Debug.Log("<color=orange>Cleared previous addressable files</color>");
            }
            
            AddressableAssetSettings.BuildPlayerContent();
            
            Debug.Log(path);
            
            await GenerateCRCValues();
        }
        
        public static async Task UpdatePreviousAddressablesBuild()
        {
            // Deletes Old Addressables
            var path = $"{Directory.GetCurrentDirectory()}/ServerData/{EditorUserBuildSettings.activeBuildTarget.ToString()}";

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                var files = Directory.GetFiles(path);

                foreach (var file in files)
                {
                    File.Delete(file);
                }

                Directory.Delete(path);
                Directory.CreateDirectory(path);
                
                Debug.Log("<color=orange>Cleared previous addressable files</color>");
            }
            
            var contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
            if (!string.IsNullOrEmpty(contentStatePath))
            {
                var azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
                AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);

                Application.logMessageReceived += HandleAddressableUpdateErrors;

                ContentUpdateScript.BuildContentUpdate(AddressableAssetSettingsDefaultObject.Settings, contentStatePath);

                Debug.Log("<color=orange>Finished updating previous build</color>");
            }
            else
            {
                AddressableAssetSettings.BuildPlayerContent();
                Debug.Log("<color=orange>No existing state found to update - finish clean addressables build</color>");
            }
            
            Application.logMessageReceived -= HandleAddressableUpdateErrors;

            await GenerateCRCValues();
        }

        private static async void HandleAddressableUpdateErrors(string logString, string stackTrace, LogType type)
        {
            // Catch errors only
            if (type == LogType.Error || type == LogType.Exception)
            {
                Debug.Log($"<color=orange>An Error occured while updating previous addressables build: {logString}</color>");
                Debug.Log($"<color=orange>Making a clean addressable build instead</color>");
                AddressableAssetSettings.BuildPlayerContent();
                await GenerateCRCValues();
            }
        }

        private static async void CloudBuild_HandleAddressableUpdateErrors(string logString, string stackTrace, LogType type)
        {
            // Catch errors only
            if (type == LogType.Error || type == LogType.Exception)
            {
                Debug.Log($"<color=orange>An Error occured while updating previous addressables build: {logString}</color>");
                Debug.Log($"<color=orange>Making a clean addressable build instead</color>");
                AddressableAssetSettings.BuildPlayerContent();
                await CloudBuild_GenerateCRCValues();
            }
        }

        public static async Task GenerateCRCValues(string addressablesBuildPath = null)
        {
            if (addressablesBuildPath == null)
            {
                string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
            }

            if (Directory.Exists(addressablesBuildPath))
            {
                DirectoryInfo dir = new DirectoryInfo(addressablesBuildPath);
                GameAddressableData data = Resources.Load<GameAddressableData>("GameAddressableData");
                data.AddressableCRCList.Clear();

                FileInfo[] files = dir.GetFiles();

                for (int i = 0; i < files.Length; i++)
                {
                    var fromFileBytes = await File.ReadAllBytesAsync(files[i].FullName);
                    data.AddressableCRCList.Add(new AddressableCRCEntry(files[i].Name, Crc32CAlgorithm.Compute(fromFileBytes)));
                }
                EditorUtility.SetDirty(data);
                AssetDatabase.Refresh();
                Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated CRC List</color>");
            }

        }
        
        public static void SwitchCurrentAddressablesProfileByName(string profileName)
       {
           AddressableAssetSettings assetSettings;
           AddressableAssetProfileSettings assetProfileSettings;
           
           //Initialize asset settings and asset profile settings
           assetSettings = AddressableAssetSettingsDefaultObject.Settings;
           assetProfileSettings = assetSettings.profileSettings;
           
           //First Validate if the given named addressable profile exists or not
           if (!assetProfileSettings.GetAllProfileNames().Contains(profileName))
           {
               throw new Exception($"No Addressable Profile exists that goes by name: {profileName}. Please enter the correct name.");
           }
           
           assetSettings.activeProfileId = assetProfileSettings.GetProfileId(profileName);
           assetSettings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
           
            //Update profile info in game asset file as well.
           GameAddressableData profileData = Resources.Load<GameAddressableData>("GameAddressableData");
           profileData.profileName = profileName;
           profileData.profileId = assetSettings.activeProfileId;
           EditorUtility.SetDirty(profileData);
           AssetDatabase.Refresh();
           Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
       }
        
        public static async Task CloudBuild_GenerateCRCValues(string addressablesBuildPath = null)
        {
            if (addressablesBuildPath == null)
            {
                string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
            }

            if (Directory.Exists(addressablesBuildPath))
            {
                DirectoryInfo dir = new DirectoryInfo(addressablesBuildPath);
                GameAddressableData data = (GameAddressableData)AssetDatabase.LoadAssetAtPath("Assets/Resources/GameAddressableData.asset", typeof(GameAddressableData));
                data.AddressableCRCList.Clear();

                FileInfo[] files = dir.GetFiles();

                for (int i = 0; i < files.Length; i++)
                {
                    var fromFileBytes = await File.ReadAllBytesAsync(files[i].FullName);
                    data.AddressableCRCList.Add(new AddressableCRCEntry(files[i].Name, Crc32CAlgorithm.Compute(fromFileBytes)));
                }
                EditorUtility.SetDirty(data);
                AssetDatabase.Refresh();
                Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated CRC List</color>");
            }
            else
            {
                Debug.Log($"<color=orange>No addressable build path found: {addressablesBuildPath}</color>");
            }
        }
        
        public static void CloudBuild_SwitchAddressablesProfileByName(string profileName)
        {
            AddressableAssetSettings assetSettings;
            AddressableAssetProfileSettings assetProfileSettings;
           
            //Initialize asset settings and asset profile settings
            assetSettings = AddressableAssetSettingsDefaultObject.Settings;
            assetProfileSettings = assetSettings.profileSettings;
           
            //First Validate if the given named addressable profile exists or not
            if (!assetProfileSettings.GetAllProfileNames().Contains(profileName))
            {
                throw new Exception($"No Addressable Profile exists that goes by name: {profileName}. Please enter the correct name.");
            }
           
            assetSettings.activeProfileId = assetProfileSettings.GetProfileId(profileName);
            assetSettings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
           
            //Update profile info in game asset file as well.
            GameAddressableData profileData = (GameAddressableData)AssetDatabase.LoadAssetAtPath("Assets/Resources/GameAddressableData.asset", typeof(GameAddressableData));
            profileData.profileName = profileName;
            profileData.profileId = assetSettings.activeProfileId;
            EditorUtility.SetDirty(profileData);
            AssetDatabase.Refresh();
            Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
        }

        public static async Task CloudBuild_BuildCleanAddressables(string addressablesBuildPath = null)
        {
            if (addressablesBuildPath == null)
            {
                string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
            }

            if (Directory.Exists(addressablesBuildPath))
            {
                var files = Directory.GetFiles(addressablesBuildPath);

                foreach (var file in files)
                {
                    File.Delete(file);
                }

                Directory.Delete(addressablesBuildPath);
                Directory.CreateDirectory(addressablesBuildPath);
                
                Debug.Log("<color=orange>Cleared previous addressable files</color>");
                
                AddressableAssetSettings.BuildPlayerContent();
            
                Debug.Log(addressablesBuildPath);
            
                await CloudBuild_GenerateCRCValues(addressablesBuildPath);
            }
        }

        public static async Task CloudBuild_UpdatePreviousAddressablesBuild(string addressablesBuildPath = null)
        {
            if (addressablesBuildPath == null)
            {
                string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                addressablesBuildPath = Application.dataPath + "/../ServerData/" + currentBuildTarget;
            }

            if (Directory.Exists(addressablesBuildPath))
            {
                var files = Directory.GetFiles(addressablesBuildPath);

                foreach (var file in files)
                {
                    File.Delete(file);
                }

                Directory.Delete(addressablesBuildPath);
                Directory.CreateDirectory(addressablesBuildPath);
                
                Debug.Log("<color=orange>Cleared previous addressable files</color>");
                
                var contentStatePath = ContentUpdateScript.GetContentStateDataPath(false);
                if (!string.IsNullOrEmpty(contentStatePath))
                {
                    var azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
                    AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);

                    Application.logMessageReceived += CloudBuild_HandleAddressableUpdateErrors;

                    ContentUpdateScript.BuildContentUpdate(AddressableAssetSettingsDefaultObject.Settings, contentStatePath);

                    Debug.Log("<color=orange>Finished updating previous build</color>");
                }
                else
                {
                    AddressableAssetSettings.BuildPlayerContent();
                    Debug.Log("<color=orange>No existing state found to update - finish clean addressables build</color>");
                }
            
                Application.logMessageReceived -= CloudBuild_HandleAddressableUpdateErrors;

                await CloudBuild_GenerateCRCValues(addressablesBuildPath);
            }
        }
    }
}
