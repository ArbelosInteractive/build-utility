using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC Store Export Settings")]
public sealed class DlcStoreExportSettings : ScriptableObject
{
    public DlcStoreExportOptions options = new DlcStoreExportOptions();

    [Tooltip("Azure Key Vault holding the Apple, Google and PlayFab upload credentials (just the name, not the URL). " +
             "Nothing secret is stored in this asset; each person signs in with their own Azure account.")]
    public string keyVaultName = "";

    [Tooltip("Optional. Azure tenant ID or domain for Sign in to Azure, needed when your account is a guest in the vault's tenant.")]
    public string azureTenant = "";

    [Tooltip("Python 3.8+ with: requests pyjwt[crypto] google-auth azure-identity azure-keyvault-secrets (Install packages in the Upload section).")]
    public string pythonExecutable = "python";

    [Tooltip("Folder of PlayFab store icons (PNG), relative to the project or absolute. Each PlayFab item is uploaded with " +
             "<product or bundle ID>.png as its thumbnail, or default_<content type>.png (e.g. default_wrestler.png) when it has none. " +
             "Leave empty to leave thumbnails alone.")]
    public string playFabIconFolder = "Assets/Editor/StoreIcons";

    /// <summary>The icon folder as a full path, or null when none is set.</summary>
    public string PlayFabIconFolderFullPath() =>
        string.IsNullOrWhiteSpace(playFabIconFolder) ? null
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), playFabIconFolder.Trim()));
}

[Serializable]
public sealed class DlcStoreExportOptions
{
    public bool playFab = true, apple = true, google = true;
    public string playFabTitleId = "";
    [Tooltip("Numeric App Store Connect app ID, not the bundle ID or Apple SKU.")]
    public string appleAppId = "";
    public string appleBaseTerritory = "";
    public List<string> appleAvailableTerritories = new List<string>();
    public bool appleAvailableInNewTerritories;
    public string googlePackageName = "";
    [Tooltip("Use the regions version published by Google for your regional prices.")]
    public string googleRegionsVersion = "";
    public List<DlcStoreExportProduct> products = new List<DlcStoreExportProduct>();
}

[Serializable]
public sealed class DlcStoreExportProduct
{
    public string productId = "";
    public string playFabContentType = "";
    public string appleReferenceName = "";
    [DlcReviewScreenshotPath]
    [Tooltip("Required for Apple exports. PNG/JPEG file path, absolute or relative to the Unity project. The export resizes a copy to 1179 x 2556 portrait PNG, preserving the full image with black padding.")]
    public string appleReviewScreenshotPath = "";
    [Tooltip("Base-territory customer price. Decimal with a dot; never inferred from an old Apple price tier.")]
    public string appleBasePrice = "";
    public string appleBaseCurrency = "";
    public bool appleFamilySharable;
    public List<DlcStoreExportLocalization> localizations = new List<DlcStoreExportLocalization>();
    public List<DlcStoreExportRegion> googleRegions = new List<DlcStoreExportRegion>();
}

[Serializable]
public sealed class DlcStoreExportLocalization
{
    [Tooltip("PlayFab language code; NEUTRAL is supplied from the Unity catalog separately.")]
    public string playFabLocale = "";
    public string appleLocale = "";
    public string googleLanguageCode = "";
    public string title = "";
    [TextArea] public string description = "";
}

[Serializable]
public sealed class DlcStoreExportRegion
{
    public string regionCode = "";
    public string currencyCode = "";
    [Tooltip("Customer price in this region's currency, with a dot decimal separator (up to nine decimal places).")]
    public string price = "";
}

public sealed class DlcReviewScreenshotPathAttribute : PropertyAttribute { }
