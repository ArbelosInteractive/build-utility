using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class DlcStoreCatalogExportTests
{
    static string screenshotFixture;
    static string ReviewScreenshotFixture()
    {
        if (screenshotFixture == null)
        {
            screenshotFixture = Path.Combine(Path.GetTempPath(), "dlcpump-review-fixture-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(screenshotFixture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4X8AAAAASUVORK5CYII="));
        }
        return screenshotFixture;
    }

    [OneTimeTearDown]
    public void CleanupScreenshotFixture()
    {
        if (screenshotFixture != null && File.Exists(screenshotFixture)) File.Delete(screenshotFixture);
        screenshotFixture = null;
    }

    public static JObject Manifest(int count = 1)
    {
        return new JObject { ["releaseId"] = "test-release", ["products"] = new JArray(Enumerable.Range(0, count).Select(i => new JObject {
            ["id"] = "pack_" + i, ["type"] = 1,
            ["defaultDescription"] = new JObject { ["title"] = "Taunt pack " + i, ["description"] = "Unlock taunt animations." },
            ["storeIDs"] = new JArray(), ["payouts"] = new JArray(new JObject { ["st"] = "animation" }) })) };
    }

    public static DlcStoreExportOptions Options(JObject manifest)
    {
        var o = new DlcStoreExportOptions { playFabTitleId = "AB123", appleAppId = "1234567890", appleBaseTerritory = "USA",
            googlePackageName = "com.example.game", googleRegionsVersion = "2022/02" };
        o.appleAvailableTerritories.Add("USA");
        DlcStoreCatalogExport.AddMissingProducts(manifest, o);
        foreach (var p in o.products)
        {
            p.appleBasePrice = "1.99"; p.appleBaseCurrency = "USD";
            p.appleReviewScreenshotPath = ReviewScreenshotFixture();
            p.localizations[0].appleLocale = "en-US";
            p.localizations[0].googleLanguageCode = "en-US";
            p.localizations[0].playFabLocale = "en-US";
            p.googleRegions.Add(new DlcStoreExportRegion { regionCode = "US", currencyCode = "USD", price = "1.990000001" });
        }
        return o;
    }

    [Test]
    public void AppleScreenshotIsRequiredButDisabledAppleDoesNotRequireIt()
    {
        var m = Manifest(); var o = Options(m);
        o.products[0].appleReviewScreenshotPath = "";
        Assert.IsTrue(DlcStoreCatalogExport.Validate(m, o).Any(e => e.Contains("Apple review screenshot")));
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Build(m, o));
        o.apple = false;
        Assert.IsEmpty(DlcStoreCatalogExport.Validate(m, o));
    }

    [Test]
    public void MissingOrInvalidScreenshotFilesBlockExport()
    {
        var m = Manifest(); var o = Options(m);
        string parent = Path.Combine(Path.GetTempPath(), "dlcpump-image-no-write-" + Guid.NewGuid().ToString("N"));
        o.products[0].appleReviewScreenshotPath = parent + ".png";
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Export(m, o, parent));
        Assert.IsFalse(Directory.Exists(parent));
        string invalid = Path.Combine(Path.GetTempPath(), "dlcpump-invalid-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllText(invalid, "not an image");
            o.products[0].appleReviewScreenshotPath = invalid;
            Assert.IsTrue(DlcStoreCatalogExport.Validate(m, o).Any(e => e.Contains("image signature")));
            Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Export(m, o, parent));
            Assert.IsFalse(Directory.Exists(parent));
        }
        finally { File.Delete(invalid); }
    }

    [Test]
    public void AppleScreenshotIsPackagedWithReservationUploadCommitAndVerification()
    {
        var m = Manifest(2); var o = Options(m);
        string parent = Path.Combine(Path.GetTempPath(), "dlcpump-images-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = DlcStoreCatalogExport.Export(m, o, parent);
            var requests = (JArray)JObject.Parse(File.ReadAllText(Path.Combine(directory, "requests.json")))["requests"];
            for (int i = 1; i <= 2; i++)
            {
                string key = i.ToString("D4");
                string image = "apple/" + key + "/review-screenshot.png";
                var original = File.ReadAllBytes(o.products[i - 1].appleReviewScreenshotPath);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(directory, image)));
                var reserve = JObject.Parse(File.ReadAllText(Path.Combine(directory, "apple/" + key + "/05-screenshot-reserve.json")));
                Assert.AreEqual(original.LongLength, (long)reserve["data"]["attributes"]["fileSize"]);
                Assert.AreEqual("review-screenshot.png", (string)reserve["data"]["attributes"]["fileName"]);
                var upload = requests.Single(r => (string)r["id"] == "apple-" + key + "-screenshot-upload");
                Assert.AreEqual(image, (string)upload["file"]);
                var commit = JObject.Parse(File.ReadAllText(Path.Combine(directory, "apple/" + key + "/06-screenshot-commit.json")));
                Assert.IsTrue((bool)commit["data"]["attributes"]["uploaded"]);
                string checksum;
                using (var md5 = System.Security.Cryptography.MD5.Create()) checksum = BitConverter.ToString(md5.ComputeHash(original)).Replace("-", "").ToLowerInvariant();
                Assert.AreEqual(checksum, (string)commit["data"]["attributes"]["sourceFileChecksum"]);
                Assert.AreEqual("{{apple-" + key + "-screenshot-reserve.data.id}}", (string)commit["data"]["id"]);
                Assert.IsTrue(requests.Any(r => (string)r["id"] == "apple-" + key + "-screenshot-verify"));
            }
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Test]
    public void ExportIncludesEverySettingsProductOutsideTheReleaseSelection()
    {
        var catalog = Manifest(3);
        var options = Options(catalog);
        var releaseOnly = Manifest(1);
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(catalog, options, (string)releaseOnly["releaseId"]);
        var files = DlcStoreCatalogExport.Build(manifest, options);
        Assert.AreEqual(1, ((JArray)releaseOnly["products"]).Count);
        Assert.AreEqual(3, ((JArray)manifest["products"]).Count);
        Assert.AreEqual(3, ((JArray)JObject.Parse(files["google/0001-batch.json"])["requests"]).Count);
        Assert.IsTrue(files.ContainsKey("playfab/0003-create.json"));
        Assert.IsTrue(files.ContainsKey("apple/0003/01-create.json"));
    }

    [Test]
    public void ExportSettingsSelectOnlyConfiguredCatalogProductsAndValidateThemAll()
    {
        var catalog = Manifest(3); var options = Options(catalog);
        options.products.RemoveAt(0);
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(catalog, options, "release");
        CollectionAssert.AreEqual(new[] { "pack_1", "pack_2" }, ((JArray)manifest["products"]).Select(p => (string)p["id"]).ToArray());
        options.products[1].appleBasePrice = "";
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Build(manifest, options));
        Assert.AreEqual(3, ((JArray)catalog["products"]).Count);
    }

    [Test]
    public void UnknownOrDuplicateSettingsProductsCannotBeSilentlyOmitted()
    {
        var catalog = Manifest(); var options = Options(catalog);
        options.products[0].productId = "unknown";
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.ConfiguredProducts(catalog, options, "release"));
        options.products[0].productId = "pack_0";
        options.products.Add(options.products[0]);
        var manifest = DlcStoreCatalogExport.ConfiguredProducts(catalog, options, "release");
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Build(manifest, options));
        options.products.Clear();
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Build(DlcStoreCatalogExport.ConfiguredProducts(catalog, options, "release"), options));
    }

    [Test]
    public void ExportsPlatformIdsWithoutChangingPlayFabFriendlyIdOrCatalog()
    {
        var m = Manifest(); var o = Options(m);
        m["products"][0]["storeIDs"] = new JArray(new JObject { ["store"] = "AppleAppStore", ["id"] = "ios.pack" }, new JObject { ["store"] = "GooglePlay", ["id"] = "android.pack" });
        string before = m.ToString();
        var files = DlcStoreCatalogExport.Build(m, o);
        var pf = JObject.Parse(files["playfab/0001-create.json"]);
        Assert.IsFalse((bool)pf["Publish"]);
        Assert.AreEqual("catalogItem", (string)pf["Item"]["Type"]);
        Assert.AreEqual("pack_0", (string)pf["Item"]["AlternateIds"][0]["Value"]);
        Assert.AreEqual("animation", (string)pf["Item"]["ContentType"]);
        Assert.AreEqual("ios.pack", (string)JObject.Parse(files["apple/0001/01-create.json"])["data"]["attributes"]["productId"]);
        var google = JObject.Parse(files["google/0001-batch.json"])["requests"][0];
        Assert.AreEqual("android.pack", (string)google["oneTimeProduct"]["productId"]);
        var price = google["oneTimeProduct"]["purchaseOptions"][0]["regionalPricingAndAvailabilityConfigs"][0]["price"];
        Assert.AreEqual("1", (string)price["units"]);
        Assert.AreEqual(990000001, (int)price["nanos"]);
        Assert.IsNull(google["oneTimeProduct"]["purchaseOptions"][0]["state"]);
        Assert.AreEqual(before, m.ToString());
    }

    [Test]
    public void SplitsGoogleBatchesAtOneHundred()
    {
        var m = Manifest(201); var o = Options(m); o.apple = o.playFab = false;
        var files = DlcStoreCatalogExport.Build(m, o);
        Assert.AreEqual(100, ((JArray)JObject.Parse(files["google/0001-batch.json"])["requests"]).Count);
        Assert.AreEqual(100, ((JArray)JObject.Parse(files["google/0002-batch.json"])["requests"]).Count);
        Assert.AreEqual(1, ((JArray)JObject.Parse(files["google/0003-batch.json"])["requests"]).Count);
    }

    [Test]
    public void AppleDependenciesUseReturnedIdsAndExactPriceSelection()
    {
        var m = Manifest(); var o = Options(m); var files = DlcStoreCatalogExport.Build(m, o);
        var requests = (JArray)JObject.Parse(files["requests.json"])["requests"];
        var lookup = requests.Single(r => (string)r["id"] == "apple-0001-pricepoints");
        StringAssert.Contains("{{apple-0001-create.data.id}}", (string)lookup["url"]);
        Assert.AreEqual("1.99", (string)lookup["select"]["equalsDecimal"]);
        var pricing = requests.Single(r => (string)r["id"] == "apple-0001-pricing");
        CollectionAssert.AreEquivalent(new[] { "apple-0001-create", "apple-0001-pricepoints" }, ((JArray)pricing["dependsOn"]).Values<string>().ToArray());
        var body = JObject.Parse(files["apple/0001/03-pricing.json"]);
        Assert.AreEqual("${price1}", (string)body["data"]["relationships"]["manualPrices"]["data"][0]["id"]);
        Assert.AreEqual("{{apple-0001-pricepoints.selected.id}}", (string)body["included"][0]["relationships"]["inAppPurchasePricePoint"]["data"]["id"]);
        foreach (var r in requests)
            foreach (string dep in (JArray)r["dependsOn"])
                Assert.Less(requests.IndexOf(requests.Single(x => (string)x["id"] == dep)), requests.IndexOf(r));
    }

    [Test]
    public void MissingFieldsBlockExportBeforeAnyFilesAreWritten()
    {
        var m = Manifest(); var o = new DlcStoreExportOptions();
        DlcStoreCatalogExport.AddMissingProducts(m, o);
        var errors = DlcStoreCatalogExport.Validate(m, o);
        Assert.GreaterOrEqual(errors.Count, 7);
        string parent = Path.Combine(Path.GetTempPath(), "dlcpump-no-write-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Export(m, o, parent));
        Assert.IsFalse(Directory.Exists(parent));
    }

    [Test]
    public void RejectsDuplicateOverridesLocalesAndUnsupportedProducts()
    {
        var m = Manifest(2); var o = Options(m);
        foreach (var p in (JArray)m["products"])
            p["storeIDs"] = new JArray(new JObject { ["store"] = "GooglePlay", ["id"] = "same_id" });
        o.products[0].localizations.Add(o.products[0].localizations[0]);
        m["products"][1]["type"] = 0;
        var errors = DlcStoreCatalogExport.Validate(m, o);
        Assert.IsTrue(errors.Any(e => e.Contains("Duplicate effective")));
        Assert.IsTrue(errors.Any(e => e.Contains("duplicate Apple locales")));
        Assert.IsTrue(errors.Any(e => e.Contains("only non-consumable")));
        Assert.Throws<InvalidOperationException>(() => DlcStoreCatalogExport.Build(m, o));
    }

    [Test]
    public void RejectsInvalidPricesIdsAndOverlongCopy()
    {
        var m = Manifest(); var o = Options(m);
        o.products[0].appleBasePrice = "1,99";
        o.products[0].googleRegions[0].price = "0";
        o.products[0].localizations[0].title = new string('x', 56);
        m["products"][0]["storeIDs"] = new JArray(new JObject { ["store"] = "GooglePlay", ["id"] = "Invalid/ID" });
        var errors = DlcStoreCatalogExport.Validate(m, o);
        Assert.IsTrue(errors.Any(e => e.Contains("Apple base")));
        Assert.IsTrue(errors.Any(e => e.Contains("Google US")));
        Assert.IsTrue(errors.Any(e => e.Contains("invalid Google product ID")));
        Assert.IsTrue(errors.Any(e => e.Contains("maximum 30")));
    }

    [Test]
    public void DisabledPlatformsDoNotRequireTheirSettings()
    {
        var m = Manifest(); var o = new DlcStoreExportOptions { apple = false, google = false, playFabTitleId = "123AB" };
        DlcStoreCatalogExport.AddMissingProducts(m, o);
        o.products[0].localizations = null;
        Assert.IsEmpty(DlcStoreCatalogExport.Validate(m, o));
        Assert.IsTrue(DlcStoreCatalogExport.Build(m, o).ContainsKey("playfab/0001-create.json"));
    }

    [Test]
    public void SeedingPreservesSettingsAndExportUsesNewDirectories()
    {
        var m = Manifest(); var o = Options(m);
        o.products[0].appleReferenceName = "Reviewed name";
        DlcStoreCatalogExport.AddMissingProducts(m, o);
        Assert.AreEqual(1, o.products.Count);
        Assert.AreEqual("Reviewed name", o.products[0].appleReferenceName);
        string parent = Path.Combine(Path.GetTempPath(), "dlcpump-export-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = DlcStoreCatalogExport.Export(m, o, parent);
            string before = File.ReadAllText(Path.Combine(first, "requests.json"));
            string second = DlcStoreCatalogExport.Export(m, o, parent);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(before, File.ReadAllText(Path.Combine(first, "requests.json")));
            Assert.IsTrue(File.Exists(Path.Combine(second, "README.md")));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }
}

public class DlcStoreExportSerializationTests
{
    [Test]
    public void SettingsPersistAndReleaseAssignmentSupportsUndoRedo()
    {
        string folder = "Assets/DlcStoreExportTest_" + Guid.NewGuid().ToString("N");
        UnityEditor.AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        try
        {
            var settings = UnityEngine.ScriptableObject.CreateInstance<DlcStoreExportSettings>();
            settings.options = DlcStoreCatalogExportTests.Options(DlcStoreCatalogExportTests.Manifest());
            string settingsPath = folder + "/Settings.asset";
            UnityEditor.AssetDatabase.CreateAsset(settings, settingsPath);
            var release = UnityEngine.ScriptableObject.CreateInstance<DlcRelease>();
            string releasePath = folder + "/Release.asset";
            UnityEditor.AssetDatabase.CreateAsset(release, releasePath);
            UnityEditor.Undo.RecordObject(release, "Assign export settings");
            release.storeExportSettings = settings;
            UnityEditor.Undo.FlushUndoRecordObjects();
            UnityEditor.Undo.PerformUndo();
            Assert.IsNull(release.storeExportSettings);
            UnityEditor.Undo.PerformRedo();
            Assert.AreEqual(settings, release.storeExportSettings);
            UnityEditor.EditorUtility.SetDirty(release);
            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.ImportAsset(settingsPath, UnityEditor.ImportAssetOptions.ForceUpdate);
            UnityEditor.AssetDatabase.ImportAsset(releasePath, UnityEditor.ImportAssetOptions.ForceUpdate);
            var loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<DlcRelease>(releasePath);
            Assert.IsNotNull(loaded.storeExportSettings);
            var options = loaded.storeExportSettings.options;
            Assert.AreEqual("1.990000001", options.products[0].googleRegions[0].price);
            Assert.AreEqual("en-US", options.products[0].localizations[0].appleLocale);
            Assert.IsTrue(File.Exists(options.products[0].appleReviewScreenshotPath));
            Assert.IsEmpty(DlcStoreCatalogExport.Validate(DlcStoreCatalogExportTests.Manifest(), options));
            var serialized = new UnityEditor.SerializedObject(loaded);
            Assert.IsNotNull(serialized.FindProperty("storeExportSettings"));
            var serializedSettings = new UnityEditor.SerializedObject(loaded.storeExportSettings);
            Assert.IsNotNull(serializedSettings.FindProperty("options"));
        }
        finally
        {
            UnityEditor.AssetDatabase.DeleteAsset(folder);
            new DlcStoreCatalogExportTests().CleanupScreenshotFixture();
        }
    }
}
