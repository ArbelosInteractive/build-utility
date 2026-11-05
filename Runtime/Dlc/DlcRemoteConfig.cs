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
        [Tooltip("PRODUCTION: public URL of the production container root, normally the CDN endpoint, e.g. " +
                 "https://example.azurefd.net/dlc-production. The service appends /<Platform>/<Application.version>/. " +
                 "Store builds read this one.")]
        public string cdnBaseUrl = "";

        [Tooltip("STAGING: public URL of the staging container root, e.g. https://example.azurefd.net/dlc-staging. " +
                 "Read only when DlcEnvironment.UseStaging(true) was called (staging builds, or the Editor's staging " +
                 "Play Mode). Left empty, staging loads no DLC instead of falling back to production.")]
        public string stagingCdnBaseUrl = "";

        /// <summary>The URL for the current <see cref="DlcEnvironment"/>, without a trailing slash.</summary>
        public string ActiveBaseUrl => DlcEnvironment.BaseUrl(this);

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
