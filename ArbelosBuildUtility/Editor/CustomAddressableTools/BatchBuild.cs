#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Arbelos.BuildUtility.Editor
{
    internal class BatchBuild
    {
        public static string buildScript = "Assets/Scripts/AddressablesRelated/CustomBuildScriptPacked.asset";
        public static string profileName = "Azure";

        public static void ChangeSettings()
        {
            string defines = "";
            string[] args = Environment.GetCommandLineArgs();

            foreach(string arg in args)
            {
                if(arg.StartsWith("-defines=", System.StringComparison.CurrentCulture))
                {
                    defines = arg.Substring("-defines=".Length);
                }
            }

            var buildSettings = EditorUserBuildSettings.selectedBuildTargetGroup;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(buildSettings, defines);
        }

        public static void BuildContent()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

            settings.activeProfileId = settings.profileSettings.GetProfileId(profileName);

            IDataBuilder builder = AssetDatabase.LoadAssetAtPath<ScriptableObject>(buildScript) as IDataBuilder;

            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new Exception(result.Error);
            }
                        
        }

    }
}
#endif