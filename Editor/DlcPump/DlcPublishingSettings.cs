using Arbelos.BuildUtility.Runtime;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Destination for Build Utility > DLC Publish. Holds no secrets: uploads sign in as the current Azure CLI user.
/// </summary>
[CreateAssetMenu(menuName = "Build Utility/DLC Publishing Settings")]
public sealed class DlcPublishingSettings : ScriptableObject
{
    [Tooltip("Storage account name (the <name> in https://<name>.blob.core.windows.net).")]
    [FormerlySerializedAs("azureAccount")]
    public string accountName = "";

    [Tooltip("STAGING blob container: Publish, Status, Rollback and the content-state baseline use it. Staging builds read it " +
             "(DlcRemoteConfig.stagingCdnBaseUrl). Lowercase letters, digits and hyphens.")]
    public string container = "dlc-staging";

    [Tooltip("PRODUCTION blob container in the same storage account. Only Promote to production writes to it; store builds " +
             "read it (DlcRemoteConfig.cdnBaseUrl).")]
    public string productionContainer = "dlc-production";

    [Tooltip("PublicRead: manifests, catalog and bundles are anonymously readable. SignedUrl is reserved for paid bundles; see IDlcUrlSigner.")]
    public DlcContentAccessMode accessMode = DlcContentAccessMode.PublicRead;

    [Tooltip("Local export folder, relative to the Unity project root. Must be outside Assets.")]
    public string stagingDirectory = "DlcContentStaging";

    public string pythonExecutable = "python";

    [Tooltip("Optional. Azure tenant ID or domain for Sign in to Azure, needed when your account is a guest in the storage account's tenant.")]
    public string azureTenant = "";

    public string AccountUrl => "https://" + (accountName ?? "").Trim() + ".blob.core.windows.net";
}
