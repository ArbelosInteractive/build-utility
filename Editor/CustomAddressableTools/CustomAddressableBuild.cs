#if UNITY_EDITOR
using UnityEditor.AddressableAssets.Build;
using UnityEditor;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine;
using UnityEngine.AddressableAssets.Initialization;
using UnityEditor.AddressableAssets.Settings;

namespace Arbelos.BuildUtility.Editor
{
    [CreateAssetMenu(fileName = "CustomBuildScriptPacked.asset", menuName = "Addressables/Content Builders/Custom Build Script")]
    public class CustomAddressableBuild : BuildScriptPackedMode
    {
        protected override TResult BuildDataImplementation<TResult>(AddressablesDataBuilderInput builderInput)
        {
            var azureFriendlyBuildTarget = GetAzureFriendlyBuildTarget();
            AddressablesRuntimeProperties.SetPropertyValue("AzureFriendlyBuildTarget", azureFriendlyBuildTarget);
            
            return base.BuildDataImplementation<TResult>(builderInput);
        }


         
        public static string GetAzureFriendlyBuildTarget()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.Android:
                    {
                        return "android";
                    }
                case BuildTarget.iOS:
                    {
                        return "ios";
                    }
                case BuildTarget.StandaloneLinux64:
                    {
                        return "standalonelinux64";
                    }
                case BuildTarget.StandaloneOSX:
                    {
                        return "standaloneosx";
                    }
                case BuildTarget.StandaloneWindows64:
                    {
                        return "standalonewindows64";
                    }
                case BuildTarget.WebGL:
                    {
                        return "webgl";
                    }
                default:
                    return "android";

            }

        }
    }
}
#endif