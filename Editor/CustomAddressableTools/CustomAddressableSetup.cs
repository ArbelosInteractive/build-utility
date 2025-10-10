using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using Arbelos.BuildUtility.Runtime;
#if !UNITY_2023_1_OR_NEWER
using UnityEditor.AddressableAssets.HostingServices;
#endif
using System.Linq;
using UnityEditor.AddressableAssets.Build;

namespace Arbelos.BuildUtility.Editor
{
    public static class CustomAddressableSetup
    {
        private static AddressableAssetSettings settings;
        private static ProjectData currentProjectData = null;
        private static CustomAddressableBuild customBuildAsset = null;

        [MenuItem("Build Utility/Setup Addressables")]
        public static void SetupAddressables()
        {
            CreateDefaultAddressableAssets();
        }

        private static void CreateDefaultAddressableAssets()
        {
            settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                currentProjectData = Resources.Load<ProjectData>("BuildUtilityProjectData");

                if (currentProjectData == null)
                {
                    Debug.LogError("BUILD UTILITY - Project Data File not found. Please create a ProjectData asset in the Resources folder and assign the values.");
                    return;
                }
                //Create the whole addressableassetdata folder structure including the settings and everything.
                settings = AddressableAssetSettings.Create(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder, "AddressableAssetSettings", true, true);
                //Create the default object.
                AddressableAssetSettingsDefaultObject.Settings = settings;

                CreateAddressableProfiles();

                CreateAddressableHostingService();

                CreateCustomAddressableBuildAsset();

                SetBuiltInDataSettings();

                //Setup Variables for settings
                settings.OverridePlayerVersion = "";
                settings.BuildRemoteCatalog = true;
                settings.DisableCatalogUpdateOnStartup = true;
                settings.MaxConcurrentWebRequests = 500;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                settings.IgnoreUnsupportedFilesInBuild = false;
                settings.UniqueBundleIds = false;
                settings.ContiguousBundles = true;
                settings.NonRecursiveBuilding = true;
#if UNITY_6000_0_OR_NEWER
                // Unity 6 + Addressables 2.7.x
                settings.BuiltInBundleNaming     = BuiltInBundleNaming.ProjectName;   // was ShaderBundleNaming
                settings.MonoScriptBundleNaming  = MonoScriptBundleNaming.ProjectName; // "Disabled" was removed
#else
                // Older Unity / Addressables
                settings.ShaderBundleNaming      = ShaderBundleNaming.ProjectName;
                settings.MonoScriptBundleNaming  = MonoScriptBundleNaming.Disabled;
#endif
                if (customBuildAsset != null)
                {
                    //Remove the last databuilder script from the settings and add the customBuidAsset as the last one.
                    settings.DataBuilders.RemoveAt(settings.DataBuilders.Count - 1);
                    settings.DataBuilders.Add(customBuildAsset);
                }
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log("Addressables setup completed.");
            }
        }

        private static void CreateAddressableProfiles()
        {
            var profiles = settings.profileSettings.GetAllProfileNames();
            if (profiles.Count <= 1)
            {
                var deploymentProfileId = settings.profileSettings.AddProfile("Deployment", settings.activeProfileId);
                var editorHostedProfileId = settings.profileSettings.AddProfile("EditorHosted", settings.activeProfileId);

                //Setup Addressable Variables
                settings.profileSettings.CreateValue("AzureFriendlyBuildTarget", "");
                settings.profileSettings.CreateValue("ProfileType", "Default");

                //SETUP DEPLOYMENT PROFILE
                settings.profileSettings.SetValue(deploymentProfileId, "BuildTarget", "[UnityEditor.EditorUserBuildSettings.activeBuildTarget]");
                settings.profileSettings.SetValue(deploymentProfileId, "Remote.BuildPath", "ServerData/[BuildTarget]");
                settings.profileSettings.SetValue(deploymentProfileId, "Remote.LoadPath", $"{currentProjectData.azureStorageAccountURL}/[AzureFriendlyBuildTarget]");
                settings.profileSettings.SetValue(deploymentProfileId, "ProfileType", "Deployment");

                //SETUP EDITOR HOSTED PROFILE
                settings.profileSettings.SetValue(editorHostedProfileId, "BuildTarget", "[UnityEditor.EditorUserBuildSettings.activeBuildTarget]");
                settings.profileSettings.SetValue(editorHostedProfileId, "Remote.BuildPath", "ServerData/[BuildTarget]");
                settings.profileSettings.SetValue(editorHostedProfileId, "Remote.LoadPath", "http://[PrivateIpAddress]:[HostingServicePort]");
                settings.profileSettings.SetValue(editorHostedProfileId, "ProfileType", "EditorHosted");

                //Set Deployment as the default profile.
                settings.activeProfileId = deploymentProfileId;
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("Addressable Profiles setup completed.");
        }

        private static void CreateAddressableHostingService()
        {
#if !UNITY_2023_1_OR_NEWER
            var hostingService = settings.HostingServicesManager.AddHostingService(typeof(HttpHostingService), "Local Hosting");
            if (hostingService == null)
            {
                Debug.LogError("Failed to create local hosting service!");
                return;
            }
            if (hostingService is HttpHostingService httpService)
            {
                // Assign a random port number
                int randomPort = Random.Range(60000, 70000);
                httpService.ResetListenPort(randomPort);
            }
#endif

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("Local Hosting Service setup completed.");
        }

        private static void CreateCustomAddressableBuildAsset()
        {
            customBuildAsset = ScriptableObject.CreateInstance<CustomAddressableBuild>();

            string savePath = AssetDatabase.GetAssetPath(currentProjectData);
            if (string.IsNullOrEmpty(savePath))
            {
                Debug.LogError("FAILED TO GENERATE CUSTOM ADDRESSABLE BUILD ASSET. ProjectData.asset not found in Resources folder. Please create a ProjectData asset in the Resources folder and assign the values.");
            }
            else
            {
                AssetDatabase.CreateAsset(customBuildAsset, savePath.Replace("ProjectData.asset", "Custom Build Script.asset"));
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static void SetBuiltInDataSettings()
        {
            // Find the Built-In Data Addressable Asset group
#if UNITY_6000_0_OR_NEWER
            // Unity 6 / Addressables 2.7.x+: use the literal
            AddressableAssetGroup builtInDataGroup = settings.FindGroup("Built In Data");
#else
            // Older Addressables: keep using the constant
            AddressableAssetGroup builtInDataGroup = settings.FindGroup(AddressableAssetSettings.PlayerDataGroupName);
#endif

            if (builtInDataGroup == null)
            {
                Debug.LogError("Could not find Built-In Data Addressable Asset group.");
                return;
            }

            // Access the PlayerDataGroupSchema
            PlayerDataGroupSchema playerDataGroupSchema = builtInDataGroup.GetSchema<PlayerDataGroupSchema>();

            if (playerDataGroupSchema == null)
            {
                Debug.LogError("Could not find PlayerDataGroupSchema in the Built-In Data group.");
                return;
            }

            // Disable "Include Resources Folders" and "Include Build Settings Scenes"
            playerDataGroupSchema.IncludeResourcesFolders = false;
            playerDataGroupSchema.IncludeBuildSettingsScenes = false;

            // Save the changes
            EditorUtility.SetDirty(builtInDataGroup);
            AssetDatabase.SaveAssets();

            Debug.Log("Built-In Data group configured successfully.");
        }
    }
}