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
        [MenuItem("Build Utility/Addressables/Build Addressables")]
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

        [MenuItem("Build Utility/Addressables/Update a previous build")]
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

        public static async Task GenerateCRCValues()
        {
            string currentBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            string path = Application.dataPath + "/../ServerData/" + currentBuildTarget;
            
            if (Directory.Exists(path))
            {
                DirectoryInfo dir = new DirectoryInfo(path);
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
        
        
        
    }
}
