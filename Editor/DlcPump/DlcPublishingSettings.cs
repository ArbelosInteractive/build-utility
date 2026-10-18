using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC Publishing Settings")]
public sealed class DlcPublishingSettings : ScriptableObject
{
    public string environment = "local-staging";
    public bool localEmulator = true;
    public string azureEndpoint = "http://127.0.0.1:10000/dlcpump";
    public string azureAccount = "dlcpump";
    public string azureContainer = "dlc-staging";
    public string releasePrefix = "releases";
    public bool publicBlobRead = true;
    [Tooltip("Environment-variable name only. Never put a storage key in an asset.")]
    public string keyEnvironmentVariable = "DLC_PUMP_AZURE_KEY";
    public string playFabTitleId = "";
    public string playFabEnvironment = "staging";
    public string nodeExecutable = "node";
    public string dependenciesDirectory = "Tools/DlcStaging";
}
