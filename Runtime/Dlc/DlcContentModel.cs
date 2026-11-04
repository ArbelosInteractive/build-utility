using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Arbelos.BuildUtility.Runtime
{
    // Wire format. Field names match the JSON written by the exporter and Tools~/DlcPublisher/publish_dlc.py.

    /// <summary>dlc-content/&lt;Platform&gt;/&lt;appVersion&gt;/products.json: the only mutable file.</summary>
    [Serializable]
    public sealed class DlcProductsIndex
    {
        public int schemaVersion;
        public string platform;
        public string appVersion;
        public string catalogBuild;
        public string updatedUtc;
        public List<DlcProductsIndexEntry> products = new List<DlcProductsIndexEntry>();
    }

    [Serializable]
    public sealed class DlcProductsIndexEntry
    {
        public string productId;
        /// <summary>Live version folder, e.g. "v2".</summary>
        public string current;
        /// <summary>Published version folders for this app version, newest first.</summary>
        public List<string> versions = new List<string>();
    }

    /// <summary>dlc-content/&lt;Platform&gt;/&lt;appVersion&gt;/&lt;productId&gt;/v&lt;N&gt;/product.json: immutable.</summary>
    [Serializable]
    public sealed class DlcProductManifest
    {
        public int schemaVersion;
        public string productId;
        public int version;
        public string platform;
        public string appVersion;
        public string releaseId;
        public string minimumClientVersion;
        public List<string> keys = new List<string>();
        public string catalogBuild;
        public string notes;
        public string publishedUtc;
    }

    public static class DlcContentPaths
    {
        public const int SchemaVersion = 1;
        public const string ProductsIndexFile = "products.json";
        public const string ProductFile = "product.json";

        static readonly Regex Segment = new Regex("^[A-Za-z0-9._-]+$");
        static readonly Regex VersionFolder = new Regex("^v[1-9][0-9]*$");

        /// <summary>A product ID, platform or app version that is safe to use as one path segment.</summary>
        public static bool IsSafeSegment(string value) =>
            !string.IsNullOrEmpty(value) && value != "." && value != ".." && Segment.IsMatch(value);

        public static bool IsVersionFolder(string value) => !string.IsNullOrEmpty(value) && VersionFolder.IsMatch(value);

        /// <summary>
        /// Platform folder name. Uses UnityEditor.BuildTarget names so it matches [BuildTarget] in the
        /// Addressables Remote.LoadPath (Android, iOS, StandaloneWindows64, ...).
        /// </summary>
        public static string CurrentPlatform()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
            switch (Application.platform)
            {
                case RuntimePlatform.Android: return "Android";
                case RuntimePlatform.IPhonePlayer: return "iOS";
                case RuntimePlatform.WindowsPlayer: return "StandaloneWindows64";
                case RuntimePlatform.OSXPlayer: return "StandaloneOSX";
                case RuntimePlatform.LinuxPlayer: return "StandaloneLinux64";
                case RuntimePlatform.WebGLPlayer: return "WebGL";
                default: return Application.platform.ToString();
            }
#endif
        }

        public static string AppVersionFolderUrl(string baseUrl, string platform, string appVersion) =>
            (baseUrl ?? "").TrimEnd('/') + "/" + platform + "/" + appVersion + "/";
    }

    /// <summary>Compares dotted versions numerically ("1.10.0" > "1.9.2"). A non-numeric part is compared as text.</summary>
    public static class DlcVersion
    {
        public static int Compare(string a, string b)
        {
            var pa = (a ?? "").Trim().Split('.');
            var pb = (b ?? "").Trim().Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string x = i < pa.Length ? pa[i] : "0", y = i < pb.Length ? pb[i] : "0";
                int c = long.TryParse(x, out long nx) && long.TryParse(y, out long ny)
                    ? nx.CompareTo(ny)
                    : string.CompareOrdinal(x, y);
                if (c != 0) return Math.Sign(c);
            }
            return 0;
        }

        /// <summary>True when the app is at least <paramref name="minimum"/>. An empty minimum always passes.</summary>
        public static bool Satisfies(string appVersion, string minimum) =>
            string.IsNullOrWhiteSpace(minimum) || Compare(appVersion, minimum) >= 0;
    }
}
