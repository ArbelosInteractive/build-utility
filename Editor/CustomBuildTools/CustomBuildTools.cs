using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.HostingServices;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Arbelos.BuildUtility.Runtime;

namespace Arbelos.BuildUtility.Editor
{
    public class CustomBuildTools : EditorWindow
    {
        //This window ref
        private static CustomBuildTools window;

        //Variables
        //Main 3 layouts
        Texture2D headerSectionTexture;
        Texture2D bodySectionTexture;
        Texture2D footerSectionTexture;

        Color headerSectionColor = new Color(163.0f / 255.0f, 163.0f / 255.0f, 163.0f / 255.0f);
        Color bodySectionColor = new Color(56.0f / 255.0f, 56.0f / 255.0f, 56.0f / 255.0f);
        Color footerSectionColor = new Color(163.0f / 255.0f, 163.0f / 255.0f, 163.0f / 255.0f);

        Rect headerSection;
        Rect bodySection;
        Rect footerSection;

        //Style Class
        GUISkin styleSkin;

        //Data Variables
        private AddressableAssetSettings assetSettings;
        private AddressableAssetProfileSettings assetProfileSettings;
        private IHostingService localHostingService;
        private int currentSelectedProfileIndex = 0;
        private string[] addressableProfileNames = new string[] { };

        //Build Variables
        BuildPlayerOptions customBuildOptions = new BuildPlayerOptions();
        bool alwaysAllowHTTP = true;
        bool developmentBuild = false;
        bool scriptDebugging = false;
        bool waitForManagedDebugger = false;
        bool autoConnectProfiler = false;
        bool deepProfilingSupport = false;
        bool buildAndRun = false;
        bool isBuilding = false;

        //Project Saved Data
        ProjectData currentProjectData = null;
        private string azureSharedKey = string.Empty;

        [MenuItem("Build Utility/Seamless Build")]
        static void OpenWindow()
        {
            //Create a window 
            window = (CustomBuildTools)GetWindow(typeof(CustomBuildTools));
            window.titleContent = new GUIContent("Seamless Build Tool");

            //Set min size of the window
            window.minSize = new Vector2(500, 500);
            window.maxSize = new Vector2(500, 500);

            //Display the window on screen
            window.Show();
        }

        private void OnEnable()
        {
            InitTextures();
            InitAuthoringVariables();
        }

        private void OnDisable()
        {
            azureSharedKey = string.Empty;
        }

        void InitTextures()
        {
            headerSectionTexture = new Texture2D(1, 1);
            headerSectionTexture.SetPixel(0, 0, headerSectionColor);
            headerSectionTexture.Apply();

            bodySectionTexture = new Texture2D(1, 1);
            bodySectionTexture.SetPixel(0, 0, bodySectionColor);
            bodySectionTexture.Apply();

            footerSectionTexture = new Texture2D(1, 1);
            footerSectionTexture.SetPixel(0, 0, footerSectionColor);
            footerSectionTexture.Apply();
        }

        void InitAuthoringVariables()
        {
            //Style Variables Setup
            //NAME OF THE CUSTOM GUI SKIN WE CREATED
            styleSkin = Resources.Load<GUISkin>("BuildUtilityGUISkin");

            //Init Addressable Profile Data
            InitAddressableProfileData();

            currentProjectData = Resources.Load<ProjectData>("BuildUtilityProjectData");

            if (currentProjectData == null)
            {
                Debug.LogError("BUILD UTILITY - Project Data File not found. Please create a ProjectData asset in the Resources folder and assign the values.");
            }
        }

        void InitAddressableProfileData()
        {
#if UNITY_2022_3_OR_NEWER
            var httpSavedOption = PlayerPrefs.GetString("BuildUtility_InsecureHTTPAllowed");
            if(string.IsNullOrEmpty(httpSavedOption))
            {
                alwaysAllowHTTP = true;
                PlayerPrefs.SetString("BuildUtility_InsecureHTTPAllowed", "true");
            }
            else
            {
                if(httpSavedOption == "true")
                {
                    alwaysAllowHTTP = true;
                }
                else if(httpSavedOption == "false")
                {
                    alwaysAllowHTTP = false;
                }
            }


            if(alwaysAllowHTTP)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            }
            else
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
            }
#endif

            assetSettings = AddressableAssetSettingsDefaultObject.Settings;
            assetProfileSettings = assetSettings.profileSettings;
            addressableProfileNames = assetProfileSettings.GetAllProfileNames().ToArray();
            var activeProfileName = assetProfileSettings.GetProfileName(assetSettings.activeProfileId);
            currentSelectedProfileIndex = Array.IndexOf(addressableProfileNames, activeProfileName);
            localHostingService = assetSettings.HostingServicesManager.HostingServices.First();
            if (addressableProfileNames[currentSelectedProfileIndex] == "Deployment")
            {
#if UNITY_2022_3_OR_NEWER
                if(!alwaysAllowHTTP)
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
                }
#endif
                if (localHostingService != null && localHostingService.IsHostingServiceRunning)
                {
                    localHostingService.StopHostingService();
                }
            }
            else if (addressableProfileNames[currentSelectedProfileIndex] == "EditorHosted")
            {
#if UNITY_2022_3_OR_NEWER
                if (!alwaysAllowHTTP)
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                }
#endif
                if (localHostingService != null && !localHostingService.IsHostingServiceRunning)
                {
                    localHostingService.StartHostingService();
                }
            }

            //Initially save the addressable profile data to make sure latest changes are saved
            UpdateAddressableProfileData(addressableProfileNames[currentSelectedProfileIndex], assetSettings.activeProfileId);
        }

        private void OnGUI()
        {
            DrawLayouts();
            DrawHeader();
            DrawBody();
            DrawFooter();
        }

        private void DrawFooter()
        {
            //This acts like a open and close curly brackets. All the content drawn inside this will be on Header Rect
            GUILayout.BeginArea(footerSection);

            GUILayout.EndArea();
        }

        private async void DrawBody()
        {
            GUILayout.BeginArea(bodySection);
            GUILayout.BeginVertical();

            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.Label($"Select Addressables Profile: ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));
            EditorGUI.BeginChangeCheck();
            currentSelectedProfileIndex = EditorGUILayout.Popup(currentSelectedProfileIndex, addressableProfileNames, GUILayout.Height(25), GUILayout.Width(200), GUILayout.ExpandWidth(false));
            if (EditorGUI.EndChangeCheck())
            {
                assetSettings.activeProfileId = assetProfileSettings.GetProfileId(addressableProfileNames[currentSelectedProfileIndex]);
                assetSettings.SetDirty(AddressableAssetSettings.ModificationEvent.ActiveProfileSet, null, true, true);
                UpdateAddressableProfileData(addressableProfileNames[currentSelectedProfileIndex], assetSettings.activeProfileId);
                if (addressableProfileNames[currentSelectedProfileIndex] == "Deployment")
                {
#if UNITY_2022_3_OR_NEWER
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
#endif
                    if (localHostingService != null && localHostingService.IsHostingServiceRunning)
                    {
                        localHostingService.StopHostingService();
                    }
                }
                else if (addressableProfileNames[currentSelectedProfileIndex] == "EditorHosted")
                {
#if UNITY_2022_3_OR_NEWER
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
#endif
                    if (localHostingService != null && !localHostingService.IsHostingServiceRunning)
                    {
                        localHostingService.StartHostingService();
                    }
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.Label($"Allow HTTP Calls?: ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));
            EditorGUI.BeginChangeCheck();
            alwaysAllowHTTP = EditorGUILayout.Toggle("", alwaysAllowHTTP);
            if (EditorGUI.EndChangeCheck())
            {
                if(alwaysAllowHTTP)
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                    PlayerPrefs.SetString("BuildUtility_InsecureHTTPAllowed", "true");
                }
                else
                {
                    PlayerSettings.insecureHttpOption = InsecureHttpOption.NotAllowed;
                    PlayerPrefs.SetString("BuildUtility_InsecureHTTPAllowed", "false");
                }
            }

            GUILayout.EndHorizontal();

            if (addressableProfileNames[currentSelectedProfileIndex] == "Deployment")
            {
                GUILayout.Space(20);

                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label($"Azure Shared Access Key: ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

                GUILayout.Space(20);

                azureSharedKey = EditorGUILayout.TextField(azureSharedKey, GUILayout.Height(25), GUILayout.Width(200), GUILayout.ExpandWidth(false));

                GUILayout.EndHorizontal();
            }

            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            GUILayout.Label($"Development Build ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

            GUILayout.Space(10);

            EditorGUI.BeginChangeCheck();
            developmentBuild =
                EditorGUILayout.Toggle("", developmentBuild);
            if (EditorGUI.EndChangeCheck())
            {
                if (developmentBuild)
                {
                    //This will add the development build option to the build options enum
                    customBuildOptions.options |= BuildOptions.Development;
                    customBuildOptions.options &= ~BuildOptions.AllowDebugging;
                    customBuildOptions.options &= ~BuildOptions.ConnectWithProfiler;
                    customBuildOptions.options &= ~BuildOptions.EnableDeepProfilingSupport;
                    EditorUserBuildSettings.waitForManagedDebugger = false;
                    scriptDebugging = false;
                    waitForManagedDebugger = false;
                    autoConnectProfiler = false;
                    deepProfilingSupport = false;
                }
                else
                {
                    //This will remove the development build option from the build options enum
                    customBuildOptions.options &= ~BuildOptions.Development;
                    customBuildOptions.options &= ~BuildOptions.AllowDebugging;
                    customBuildOptions.options &= ~BuildOptions.ConnectWithProfiler;
                    customBuildOptions.options &= ~BuildOptions.EnableDeepProfilingSupport;
                    EditorUserBuildSettings.waitForManagedDebugger = false;
                    scriptDebugging = false;
                    waitForManagedDebugger = false;
                    autoConnectProfiler = false;
                    deepProfilingSupport = false;
                }
            }

            GUILayout.EndHorizontal();
            if (developmentBuild)
            {
                GUILayout.Space(20);

                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label($"Script Debugging ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

                GUILayout.Space(10);

                EditorGUI.BeginChangeCheck();
                scriptDebugging =
                    EditorGUILayout.Toggle("", scriptDebugging);
                if (EditorGUI.EndChangeCheck())
                {
                    if (scriptDebugging)
                    {
                        customBuildOptions.options |= BuildOptions.AllowDebugging;
                        EditorUserBuildSettings.waitForManagedDebugger = false;
                        waitForManagedDebugger = false;
                    }
                    else
                    {
                        customBuildOptions.options &= ~BuildOptions.AllowDebugging;
                        EditorUserBuildSettings.waitForManagedDebugger = false;
                        waitForManagedDebugger = false;
                    }
                }

                GUILayout.EndHorizontal();

                if (scriptDebugging)
                {
                    GUILayout.Space(20);

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(20);
                    GUILayout.Label($"Wait for Managed Debugger ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

                    GUILayout.Space(10);

                    EditorGUI.BeginChangeCheck();
                    waitForManagedDebugger =
                        EditorGUILayout.Toggle("", waitForManagedDebugger);
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (waitForManagedDebugger)
                        {
                            EditorUserBuildSettings.waitForManagedDebugger = true;
                        }
                        else
                        {
                            EditorUserBuildSettings.waitForManagedDebugger = false;
                        }
                    }

                    GUILayout.EndHorizontal();
                }


                //AUTO CONNECT PROFILER SETTING
                GUILayout.Space(20);

                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label($"Auto Connect Profiler ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

                GUILayout.Space(10);

                EditorGUI.BeginChangeCheck();
                autoConnectProfiler =
                    EditorGUILayout.Toggle("", autoConnectProfiler);
                if (EditorGUI.EndChangeCheck())
                {
                    if (autoConnectProfiler)
                    {
                        customBuildOptions.options |= BuildOptions.ConnectWithProfiler;
                    }
                    else
                    {
                        customBuildOptions.options &= ~BuildOptions.ConnectWithProfiler;
                    }
                }

                GUILayout.EndHorizontal();

                // DEEP PROFILING SETTING
                GUILayout.Space(20);

                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUILayout.Label($"Deep Profiling Support ", styleSkin.GetStyle("BuildToolLabel"), GUILayout.ExpandWidth(false));

                GUILayout.Space(10);

                EditorGUI.BeginChangeCheck();
                deepProfilingSupport =
                    EditorGUILayout.Toggle("", deepProfilingSupport);
                if (EditorGUI.EndChangeCheck())
                {
                    if (deepProfilingSupport)
                    {
                        customBuildOptions.options |= BuildOptions.EnableDeepProfilingSupport;
                    }
                    else
                    {
                        customBuildOptions.options &= ~BuildOptions.EnableDeepProfilingSupport;
                    }
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Space(20);
            if (GUILayout.Button("Build", GUILayout.Height(35), GUILayout.Width(90), GUILayout.ExpandWidth(false)))
            {
                buildAndRun = false;
                if (!isBuilding)
                {
                    StartBuild();
                }
                else
                {
                    Debug.LogWarning("Build already in progress.");
                }
            }

            GUILayout.Space(35);

            if (GUILayout.Button("Build and Run", GUILayout.Height(35), GUILayout.Width(90), GUILayout.ExpandWidth(false)))
            {
                buildAndRun = true;
                if (!isBuilding)
                {
                    StartBuild();
                }
                else
                {
                    Debug.LogWarning("Build already in progress.");
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawHeader()
        {
            //This acts like a open and close curly brackets. All the content drawn inside this will be on Header Rect
            GUILayout.BeginArea(headerSection);
            GUILayout.Label("Seamless Build Tool", styleSkin.GetStyle("BuildToolHeader"));
            GUILayout.EndArea();
        }

        private void DrawLayouts()
        {
            //if any of the textures are null, re-init them
            if (headerSectionTexture == null || bodySectionTexture == null || footerSectionTexture == null)
            {
                InitTextures();
            }

            //HEADER
            //Starting from the top left
            headerSection.x = 0.0f;
            headerSection.y = 0.0f;
            headerSection.width = window.maxSize.x;
            headerSection.height = window.maxSize.y * 0.10f;
            GUI.DrawTexture(headerSection, headerSectionTexture);

            //BODY
            bodySection.x = 0.0f;
            bodySection.y = headerSection.height;
            bodySection.width = window.maxSize.x;
            bodySection.height = window.maxSize.y * 0.85f;
            GUI.DrawTexture(bodySection, bodySectionTexture);

            //FOOTER
            footerSection.x = 0.0f;
            footerSection.y = bodySection.height + headerSection.height;
            footerSection.width = window.maxSize.x;
            footerSection.height = window.maxSize.y * 0.05f;
            GUI.DrawTexture(footerSection, footerSectionTexture);
        }

        private void UpdateAddressableProfileData(string profileName, string profileId)
        {
            GameAddressableData profileData = Resources.Load<GameAddressableData>("GameAddressableData");
            profileData.profileName = profileName;
            profileData.profileId = profileId;
            EditorUtility.SetDirty(profileData);
            AssetDatabase.Refresh();
            Debug.Log("<color=orange>Finished Saving Addressable Game Data file with updated profile info</color>");
        }

        private async Task BuildAddressables()
        {
            await CustomAddressableTools.BuildAddressables();
        }

        private void StartBuild()
        {
            EditorApplication.update += BuildPlayer;
        }

        private async void BuildPlayer()
        {
            if (!isBuilding)
            {
                isBuilding = true;
                // Remove the delegate to ensure it runs only once
                EditorApplication.update -= BuildPlayer;

                SetupAndroidKeystore();

                await BuildAddressables();

                if (addressableProfileNames[currentSelectedProfileIndex] == "Deployment")
                {
                    await AzureUtilities.UploadAddressables(azureSharedKey);
                }

                string path = "";
                //Copy current build player settings
                customBuildOptions = BuildPlayerWindow.DefaultBuildMethods.GetBuildPlayerOptions(customBuildOptions);
                path = customBuildOptions.locationPathName;

                if (buildAndRun)
                {
                    Debug.Log("<color=orange>Building Player and will auto run when completed.</color>");
                    customBuildOptions.options |= BuildOptions.AutoRunPlayer;
                    customBuildOptions.options &= ~BuildOptions.ShowBuiltPlayer;
                }
                else if (!buildAndRun)
                {
                    Debug.Log("<color=orange>Building Player.</color>");
                    customBuildOptions.options |= BuildOptions.ShowBuiltPlayer;
                    customBuildOptions.options &= ~BuildOptions.AutoRunPlayer;
                }
                if (!String.IsNullOrEmpty(path))
                {
                    await BuildGame();
                }
                else
                {
                    // Reset the building flag
                    isBuilding = false;
                }
                // Reset the building flag
                isBuilding = false;
            }
        }

        private async Task BuildGame()
        {
            var buildCompletionSource = new TaskCompletionSource<bool>();

            // Start the build process
            BuildReport report = null;
            EditorApplication.update += OnBuildComplete;

            // This function will be called in each editor update loop until the build is complete
            void OnBuildComplete()
            {
                // Check if the build has completed
                if (report != null)
                {
                    // Unsubscribe from the update event
                    EditorApplication.update -= OnBuildComplete;
                    // Set the task completion source result
                    buildCompletionSource.SetResult(true);
                }
            }

            // Run the build synchronously
            report = BuildPipeline.BuildPlayer(customBuildOptions);

            // Await the task completion source
            await buildCompletionSource.Task;

            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log("Build succeeded: " + summary.totalSize + " bytes");
            }

            if (summary.result == BuildResult.Failed)
            {
                Debug.Log("Build failed");
            }

            // Reset the building flag
            isBuilding = false;
        }

        private void SetupAndroidKeystore()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
            {
                if (string.IsNullOrEmpty(currentProjectData.androidKeyStoreFilePath) || string.IsNullOrEmpty(currentProjectData.androidKeyStoreAliasName) || string.IsNullOrEmpty(currentProjectData.androidKeyStoreAliasPassword) || string.IsNullOrEmpty(currentProjectData.androidKeyStorePassword))
                {
                    Debug.LogError("BUILD UTILITY - Project Data file contains null values.");
                    return;
                }
                PlayerSettings.Android.keystoreName = currentProjectData.androidKeyStoreFilePath;
                PlayerSettings.Android.keystorePass = currentProjectData.androidKeyStorePassword;
                PlayerSettings.Android.keyaliasName = currentProjectData.androidKeyStoreAliasName;
                PlayerSettings.Android.keyaliasPass = currentProjectData.androidKeyStoreAliasPassword;
            }
        }
    }
}
