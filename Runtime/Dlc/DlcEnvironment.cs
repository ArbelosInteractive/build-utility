using UnityEngine;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.Scripting;

namespace Arbelos.BuildUtility.Runtime
{
    /// <summary>
    /// Which DLC content container the game reads: production (the default) or staging.
    /// <para>
    /// The Addressables Remote.LoadPath is read at runtime through <see cref="LoadPathToken"/>, so one catalog and one
    /// set of bundles work from both containers, and promoting staging to production can copy the tested files
    /// unchanged. Set <see cref="UseStaging"/> before Addressables initializes and before
    /// <see cref="DlcContentService"/> is created, e.g. from a BeforeSceneLoad / BeforeSplashScreen
    /// RuntimeInitializeOnLoadMethod. Only staging builds and the Editor should ever call it.
    /// </para>
    /// </summary>
    [Preserve]
    public static class DlcEnvironment
    {
        /// <summary>The Addressables property name of <see cref="ContentUrl"/>.</summary>
        public const string PropertyName = "Arbelos.BuildUtility.Runtime.DlcEnvironment.ContentUrl";

        /// <summary>What Remote.LoadPath starts with: <c>{…ContentUrl}/[BuildTarget]/[UnityEngine.Application.version]</c>.</summary>
        public const string LoadPathToken = "{" + PropertyName + "}";

        /// <summary>Remote.LoadPath for the DLC pipeline.</summary>
        public const string RemoteLoadPath = LoadPathToken + "/[BuildTarget]/[UnityEngine.Application.version]";

        static bool staging;
        static DlcRemoteConfig config;

        /// <summary>True when the game reads the staging container.</summary>
        public static bool Staging => staging;

        /// <summary>Switches between the staging and production containers. Call before Addressables initializes.</summary>
        public static void UseStaging(bool value)
        {
            staging = value;
            AddressablesRuntimeProperties.SetPropertyValue(PropertyName, ContentUrl);
        }

        /// <summary>
        /// The container root the game reads, without a trailing slash: <see cref="DlcRemoteConfig.stagingCdnBaseUrl"/>
        /// in staging, else <see cref="DlcRemoteConfig.cdnBaseUrl"/>. Empty when that URL isn't set: staging never falls
        /// back to production.
        /// </summary>
        [Preserve]
        public static string ContentUrl => BaseUrl(Config);

        /// <summary>The base URL <paramref name="remote"/> gives in the current environment.</summary>
        public static string BaseUrl(DlcRemoteConfig remote) =>
            remote ? ((staging ? remote.stagingCdnBaseUrl : remote.cdnBaseUrl) ?? "").Trim().TrimEnd('/') : "";

        /// <summary>"staging" or "production", for logs and labels.</summary>
        public static string Name => staging ? "staging" : "production";

        static DlcRemoteConfig Config => config ? config : config = Resources.Load<DlcRemoteConfig>("DlcRemoteConfig");

        // Enter Play Mode without a domain reload keeps statics: start every session in production.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlayMode()
        {
            staging = false;
            config = null;
            AddressablesRuntimeProperties.ClearCachedPropertyValues();
        }
    }
}
