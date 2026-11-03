using UnityEngine;

namespace Arbelos.BuildUtility.Runtime
{
    public enum DlcContentAccessMode
    {
        /// <summary>products.json, product.json, the catalog and bundles are anonymously readable (container access level "blob").</summary>
        PublicRead = 0,

        /// <summary>Reserved for paid content. Requires an <see cref="IDlcUrlSigner"/>; see the note on that interface.</summary>
        SignedUrl = 1,
    }

    /// <summary>
    /// Runtime settings for <see cref="DlcContentService"/>. Holds no secrets: only the public base URL.
    /// </summary>
    [CreateAssetMenu(menuName = "Build Utility/DLC Remote Config")]
    public sealed class DlcRemoteConfig : ScriptableObject
    {
        [Tooltip("Public URL of the dlc-content container root, normally the CDN endpoint, e.g. https://example.azurefd.net/dlc-content. " +
                 "The service appends /<Platform>/<Application.version>/. Must match the Addressables Remote.LoadPath prefix.")]
        public string cdnBaseUrl = "";

        public DlcContentAccessMode accessMode = DlcContentAccessMode.PublicRead;

        [Min(1)] public int requestTimeoutSeconds = 20;
    }

    // ---------------------------------------------------------------------------------------------
    // SIGNED URLS (not implemented yet): extension point for paid bundles.
    //
    // Implement this, assign it to DlcContentService.UrlSigner and set accessMode = SignedUrl.
    // The service then signs every products.json / product.json request it makes. Bundles and the
    // catalog are downloaded by Addressables itself, so they need signing as well, through
    // Addressables.WebRequestOverride or Addressables.ResourceManager.InternalIdTransformFunc.
    // Don't put SAS tokens or account keys in the app. Get short-lived signatures from your backend.
    // ---------------------------------------------------------------------------------------------
    public interface IDlcUrlSigner
    {
        string Sign(string url);
    }
}
