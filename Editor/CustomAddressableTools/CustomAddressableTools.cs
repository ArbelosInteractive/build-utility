using System;
using System.Collections.Generic;
using System.IO;
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
            }
            
            AddressableAssetSettings.BuildPlayerContent();
            
            Debug.Log(path);
            
            await GenerateCRCValues();
        }
        
        public static void AzureFriendlyUpdatePreviousBuild()
        {
            var path = ContentUpdateScript.GetContentStateDataPath(true);
            if (!string.IsNullOrEmpty(path))
            {
                var azureFriendlyBuildTarget = CustomAddressableBuild.GetAzureFriendlyBuildTarget();
                AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);

                ContentUpdateScript.BuildContentUpdate(AddressableAssetSettingsDefaultObject.Settings, path);

                Debug.Log("<color=orange>Finished updating previous build</color>");
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
            var profileDatas = Resources.LoadAll<GameAddressableData>("GameAddressableData").ToList();
            GameAddressableData profileData = profileDatas[0];
           profileData.profileName = profileName;
           profileData.profileId = assetSettings.activeProfileId;
           EditorUtility.SetDirty(profileData);
           AssetDatabase.Refresh();
           Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
       }
       
    }
}
