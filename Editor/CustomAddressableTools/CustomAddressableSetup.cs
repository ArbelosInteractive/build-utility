using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using Arbelos.BuildUtility.Runtime;
using UnityEditor.AddressableAssets.HostingServices;
using System.Linq;

namespace Arbelos.BuildUtility.Editor
{
    public static class CustomAddressableSetup
    {
        private static AddressableAssetSettings settings;
        private static ProjectData currentProjectData = null;
        private static IHostingService localHostingService;

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
                settings = AddressableAssetSettings.Create(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,"AddressableAssetSettings", true, true);
                //Create the default object.
                AddressableAssetSettingsDefaultObject.Settings = settings;

                CreateAddressableProfiles();

                CreateAddressableHostingService();

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
                settings.ShaderBundleNaming = UnityEditor.AddressableAssets.Build.ShaderBundleNaming.ProjectName;
                settings.MonoScriptBundleNaming = UnityEditor.AddressableAssets.Build.MonoScriptBundleNaming.Disabled;
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log("Addressables setup completed.");
            }
        }

        private static void CreateAddressableProfiles()
        {
            var profiles = settings.profileSettings.GetAllProfileNames();
            if(profiles.Count <= 1)
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

            var hostingService = settings.HostingServicesManager.AddHostingService(typeof(HttpHostingService), "Local Hosting");
            if (hostingService == null)
            {
                Debug.LogError("Failed to create local hosting service!");
                return;
            }
            if(hostingService is HttpHostingService httpService)
            {
                // Assign a random port number
                int randomPort = Random.Range(60000, 70000);
                httpService.ResetListenPort(randomPort);
            }


            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("Local Hosting Service setup completed.");
        }
    }
}
