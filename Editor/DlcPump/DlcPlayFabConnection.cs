using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC PlayFab Connection")]
public sealed class DlcPlayFabConnection : ScriptableObject
{
    [Tooltip("A name for this title, shown in every confirmation, e.g. Staging or Production.")]
    public string environmentName = "Staging";
    public string titleId = "";
    [Tooltip("Key Vault holding the title's secret key (the same vault as the IAP uploader). The secret is read as the signed-in " +
             "Azure CLI user when you review; it's never stored. Leave empty to use the environment variable below instead.")]
    public string keyVaultName = "";
    [Tooltip("Secret name in the Key Vault: playfab-staging-secret-key for the staging title, playfab-secret-key for production.")]
    public string keyVaultSecretName = "playfab-staging-secret-key";
    [Tooltip("Only when no Key Vault is set: name of a machine/user environment variable containing the title secret. Never paste the secret into this asset.")]
    public string secretEnvironmentVariable = "DLC_PUMP_PLAYFAB_SECRET";
    [Tooltip("Enable only after the ownership-safe game client is deployed to the players receiving these waves.")]
    public bool ownershipSafeClientDeployed;
}
