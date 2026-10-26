using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Build Utility/DLC Store Export Settings")]
public sealed class DlcStoreExportSettings : ScriptableObject
{
    public DlcStoreExportOptions options = new DlcStoreExportOptions();
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
