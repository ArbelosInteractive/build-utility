using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC PlayFab Connection")]
public sealed class DlcPlayFabConnection : ScriptableObject
{
    public string environmentName = "Test";
    public string titleId = "";
    [Tooltip("Name of a machine/user environment variable containing the title secret. Never paste the secret into this asset.")]
    public string secretEnvironmentVariable = "DLC_PUMP_PLAYFAB_SECRET";
    [Tooltip("Enable only after the ownership-safe game client is deployed to the players receiving these waves.")]
    public bool ownershipSafeClientDeployed;
}
