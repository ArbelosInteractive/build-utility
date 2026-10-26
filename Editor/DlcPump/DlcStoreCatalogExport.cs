using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

// Catalog mapping and validation. No credentials, HTTP calls or Unity asset changes.
public static class DlcStoreCatalogExport
{
    const string Apple = "https://api.appstoreconnect.apple.com";
    const string Google = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";
    const string LocalePattern = @"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$";

    public static JObject ConfiguredProducts(JObject catalog, DlcStoreExportOptions options, string releaseId)
    {
        if (options?.products == null) throw new InvalidOperationException("Product export settings are required.");
        var products = Products(catalog).ToArray();
        var configured = new JArray();
        foreach (var setting in options.products)
        {
            if (setting == null || string.IsNullOrWhiteSpace(setting.productId))
                throw new InvalidOperationException("Every export settings product must have a product ID.");
            var matches = products.Where(p => (string)p["id"] == setting.productId).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Export settings product must resolve uniquely in the IAP catalog: " + setting.productId);
            configured.Add(matches[0].DeepClone());
        }
        return new JObject { ["releaseId"] = releaseId, ["products"] = configured };
    }

    public static void AddMissingProducts(JObject manifest, DlcStoreExportOptions options)
    {
        foreach (var p in Products(manifest))
        {
            string id = (string)p["id"];
            if (options.products.Any(x => x != null && x.productId == id)) continue;
            options.products.Add(new DlcStoreExportProduct {
                productId = id, playFabContentType = (string)p["payouts"]?.FirstOrDefault()?["st"] ?? "",
                appleReferenceName = (string)p["defaultDescription"]?["title"] ?? "",
                appleReviewScreenshotPath = (string)p["screenshotPath"] ?? "",
                localizations = new List<DlcStoreExportLocalization> { new DlcStoreExportLocalization {
                    title = (string)p["defaultDescription"]?["title"] ?? "",
                    description = (string)p["defaultDescription"]?["description"] ?? "" } }
            });
        }
    }

    public static List<string> Validate(JObject manifest, DlcStoreExportOptions o)
    {
        var errors = new List<string>();
        if (o == null) return new List<string> { "Select store export settings." };
        if (!o.playFab && !o.apple && !o.google) errors.Add("Select at least one export platform.");
        if (o.playFab && !Matches(o.playFabTitleId, @"^[A-Fa-f0-9]+$")) errors.Add("PlayFab title ID is required (hexadecimal).");
        if (o.apple)
        {
            if (!Matches(o.appleAppId, @"^[0-9]+$")) errors.Add("Numeric App Store Connect app ID is required.");
            if (!Matches(o.appleBaseTerritory, @"^[A-Z]{3}$")) errors.Add("Apple base territory must be a three-letter territory ID (e.g. USA).");
            if (o.appleAvailableTerritories == null || o.appleAvailableTerritories.Count == 0) errors.Add("Select Apple available territories.");
            else
            {
                if (o.appleAvailableTerritories.Any(t => !Matches(t, @"^[A-Z]{3}$"))) errors.Add("Apple available territories must be three-letter IDs.");
                if (o.appleAvailableTerritories.Distinct().Count() != o.appleAvailableTerritories.Count) errors.Add("Duplicate Apple territories.");
                if (!o.appleAvailableTerritories.Contains(o.appleBaseTerritory)) errors.Add("Apple base territory must be available.");
            }
        }
        if (o.google)
        {
            if (!Matches(o.googlePackageName, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$")) errors.Add("Google application package name is required.");
            if (!Matches(o.googleRegionsVersion, @"^[0-9]+/[0-9]+$")) errors.Add("Google regions version is required (e.g. the version from Google's supported-regions documentation).");
        }
        var products = Products(manifest).ToArray();
        if (products.Length == 0) errors.Add("Select at least one product.");
        if (products.Select(p => (string)p["id"]).Distinct().Count() != products.Length) errors.Add("Duplicate selected product IDs.");
        if (o.products == null) { errors.Add("Product export settings are required."); return errors; }
        foreach (var p in products)
        {
            string id = (string)p["id"];
            if (string.IsNullOrWhiteSpace(id)) { errors.Add("Empty catalog product ID."); continue; }
            if ((int?)p["type"] != 1) errors.Add(id + ": only non-consumable DLC is supported.");
            var configs = o.products.Where(x => x != null && x.productId == id).ToArray();
            if (configs.Length != 1) { errors.Add(id + ": add exactly one product export configuration."); continue; }
            var c = configs[0];
            var locales = c.localizations ?? new List<DlcStoreExportLocalization>();
            if (o.playFab)
            {
                CheckText(errors, id + " PlayFab NEUTRAL title", (string)p["defaultDescription"]?["title"], 512);
                CheckText(errors, id + " PlayFab NEUTRAL description", (string)p["defaultDescription"]?["description"], 10000);
                if (string.IsNullOrWhiteSpace(c.playFabContentType)) errors.Add(id + ": PlayFab content type is required.");
            }
            if (o.apple)
            {
                CheckText(errors, id + " Apple reference name", c.appleReferenceName, 64);
                try { ReadScreenshot(c.appleReviewScreenshotPath); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException)
                { errors.Add(id + ": Apple review screenshot: " + e.Message); }
                CheckId(errors, id, StoreId(p, "AppleAppStore"), @"^[A-Za-z0-9_.-]{1,100}$", "Apple");
                CheckPrice(errors, id + " Apple base", c.appleBasePrice, c.appleBaseCurrency);
                CheckLocales(errors, id, locales, l => l.appleLocale, "Apple", 30, 45);
            }
            if (o.google)
            {
                CheckId(errors, id, StoreId(p, "GooglePlay"), @"^[a-z0-9][a-z0-9_.]{0,39}$", "Google");
                CheckLocales(errors, id, locales, l => l.googleLanguageCode, "Google", 55, 200);
                if (c.googleRegions == null || c.googleRegions.Count == 0) errors.Add(id + ": Google regional prices are required.");
                else
                {
                    if (c.googleRegions.Where(r => r != null).Select(r => r.regionCode).Distinct().Count() != c.googleRegions.Count) errors.Add(id + ": duplicate or null Google regions.");
                    foreach (var r in c.googleRegions.Where(r => r != null))
                    {
                        if (!Matches(r.regionCode, @"^[A-Z]{2}$")) errors.Add(id + ": Google region must be a two-letter country code.");
                        CheckPrice(errors, id + " Google " + r.regionCode, r.price, r.currencyCode);
                    }
                }
            }
            if (o.playFab)
            {
                var pf = locales.Where(l => l != null && !string.IsNullOrEmpty(l.playFabLocale)).ToList();
                CheckLocales(errors, id, pf, l => l.playFabLocale, "PlayFab", 512, 10000, false);
                if (pf.Any(l => l.playFabLocale == "NEUTRAL")) errors.Add(id + ": NEUTRAL comes from the Unity catalog; use language codes for additional translations.");
            }
            foreach (string store in new[] { "AppleAppStore", "GooglePlay" })
            {
                if ((store == "AppleAppStore" && !o.apple) || (store == "GooglePlay" && !o.google)) continue;
                if ((p["storeIDs"] as JArray)?.Count(x => (string)x["store"] == store) > 1) errors.Add(id + ": duplicate " + store + " overrides.");
            }
        }
        foreach (string store in new[] { "AppleAppStore", "GooglePlay" })
            if ((store == "AppleAppStore" && o.apple) || (store == "GooglePlay" && o.google))
                if (products.Select(p => StoreId(p, store)).Distinct().Count() != products.Length) errors.Add("Duplicate effective product IDs for " + store + ".");
        return errors;
    }

    public static Dictionary<string, string> Build(JObject manifest, DlcStoreExportOptions o)
        => Build(manifest, o, DlcAppleScreenshot.Prepare);

    // Explicit processor overload keeps mapping/file tests independent of Unity's native image codec.
    public static Dictionary<string, string> Build(JObject manifest, DlcStoreExportOptions o, Func<byte[], byte[]> prepareAppleScreenshot)
        => BuildPackage(manifest, o, prepareAppleScreenshot, out _);

    static Dictionary<string, string> BuildPackage(JObject manifest, DlcStoreExportOptions o, Func<byte[], byte[]> prepareAppleScreenshot, out Dictionary<string, byte[]> screenshots)
    {
        var errors = Validate(manifest, o);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        screenshots = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var preparedImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = new JArray();
        var googleRequests = new List<JObject>();
        var selected = Products(manifest).OrderBy(p => (string)p["id"], StringComparer.Ordinal).ToArray();
        int number = 0;
        foreach (var p in selected)
        {
            string id = (string)p["id"], key = (++number).ToString("D4", CultureInfo.InvariantCulture);
            var c = o.products.Single(x => x != null && x.productId == id);
            if (o.playFab)
            {
                var title = new JObject { ["NEUTRAL"] = p["defaultDescription"]["title"].DeepClone() };
                var description = new JObject { ["NEUTRAL"] = p["defaultDescription"]["description"].DeepClone() };
                foreach (var l in (c.localizations ?? new List<DlcStoreExportLocalization>()).Where(l => l != null && !string.IsNullOrEmpty(l.playFabLocale)))
                { title[l.playFabLocale] = l.title; description[l.playFabLocale] = l.description; }
                Add(files, requests, "playfab-" + key, "PlayFab", "POST", "https://" + o.playFabTitleId + ".playfabapi.com/Catalog/CreateDraftItem",
                    "playfab/" + key + "-create.json", new JObject { ["Publish"] = false, ["Item"] = new JObject {
                        ["Type"] = "catalogItem", ["ContentType"] = c.playFabContentType, ["Title"] = title, ["Description"] = description,
                        ["AlternateIds"] = new JArray(new JObject { ["Type"] = "FriendlyId", ["Value"] = id }) } }, id);
            }
            if (o.apple)
            {
                // Resize each source once, then use the final bytes for size, checksum and exported image.
                string sourcePath = Path.GetFullPath(c.appleReviewScreenshotPath);
                if (!preparedImages.TryGetValue(sourcePath, out var bytes))
                {
                    if (prepareAppleScreenshot == null) throw new ArgumentNullException(nameof(prepareAppleScreenshot));
                    bytes = prepareAppleScreenshot(ReadScreenshot(sourcePath));
                    if (bytes == null) throw new InvalidOperationException("Apple screenshot processing returned no image.");
                    preparedImages.Add(sourcePath, bytes);
                }
                string image = "apple/" + key + "/review-screenshot" + ScreenshotExtension(bytes);
                screenshots.Add(image, bytes);
                BuildApple(files, requests, p, c, o, key, image, bytes);
            }
            if (o.google)
            {
                googleRequests.Add(new JObject { ["allowMissing"] = true,
                    ["updateMask"] = "listings,purchaseOptions", ["regionsVersion"] = new JObject { ["version"] = o.googleRegionsVersion },
                    ["oneTimeProduct"] = new JObject { ["packageName"] = o.googlePackageName, ["productId"] = StoreId(p, "GooglePlay"),
                        ["listings"] = new JArray(c.localizations.Where(l => l != null && !string.IsNullOrEmpty(l.googleLanguageCode)).Select(l => new JObject {
                            ["languageCode"] = l.googleLanguageCode, ["title"] = l.title, ["description"] = l.description })),
                        ["purchaseOptions"] = new JArray(new JObject { ["purchaseOptionId"] = "buy",
                            ["buyOption"] = new JObject { ["legacyCompatible"] = true, ["multiQuantityEnabled"] = false },
                            ["regionalPricingAndAvailabilityConfigs"] = new JArray(c.googleRegions.Select(r => new JObject {
                                ["regionCode"] = r.regionCode, ["price"] = Money(r.price, r.currencyCode), ["availability"] = "AVAILABLE" })) }) } });
            }
        }
        for (int start = 0; start < googleRequests.Count; start += 100)
        {
            string key = (start / 100 + 1).ToString("D4", CultureInfo.InvariantCulture);
            Add(files, requests, "google-" + key, "Google", "POST", Google + o.googlePackageName + "/oneTimeProducts:batchUpdate",
                "google/" + key + "-batch.json", new JObject { ["requests"] = new JArray(googleRequests.Skip(start).Take(100)) }, null);
        }
        files.Add("requests.json", new JObject { ["schemaVersion"] = 1, ["releaseId"] = manifest["releaseId"]?.DeepClone(),
            ["exportOnly"] = true, ["requests"] = requests }.ToString());
        files.Add("validation.json", new JObject { ["schemaVersion"] = 1, ["valid"] = true, ["productCount"] = selected.Length,
            ["errors"] = new JArray(), ["notes"] = new JArray("Local validation only; remote IDs, prices, permissions and territory support must be verified by the operator.",
                "Apple response bindings must be resolved in request bodies and URLs before submission.") }.ToString());
        files.Add("README.md", Instructions);
        return files;
    }

    static void BuildApple(Dictionary<string, string> files, JArray requests, JToken p, DlcStoreExportProduct c, DlcStoreExportOptions o, string key, string image, byte[] screenshot)
    {
        string id = (string)p["id"], create = "apple-" + key + "-create", item = "{{" + create + ".data.id}}";
        string folder = "apple/" + key + "/";
        files.Add(folder + "00-pricepoint.json", new JObject { ["territory"] = o.appleBaseTerritory, ["customerPrice"] = c.appleBasePrice }.ToString());
        Add(files, requests, create, "Apple", "POST", Apple + "/v2/inAppPurchases", folder + "01-create.json",
            Resource("inAppPurchases", new JObject { ["name"] = c.appleReferenceName, ["productId"] = StoreId(p, "AppleAppStore"),
                ["inAppPurchaseType"] = "NON_CONSUMABLE", ["familySharable"] = c.appleFamilySharable },
                new JObject { ["app"] = Relation("apps", o.appleAppId) }), id);
        int index = 0;
        foreach (var l in c.localizations.Where(l => l != null && !string.IsNullOrEmpty(l.appleLocale)))
            Add(files, requests, "apple-" + key + "-locale-" + (++index), "Apple", "POST", Apple + "/v1/inAppPurchaseLocalizations",
                folder + "02-localization-" + index.ToString("D2") + ".json",
                Resource("inAppPurchaseLocalizations", new JObject { ["name"] = l.title, ["description"] = l.description, ["locale"] = l.appleLocale },
                    new JObject { ["inAppPurchaseV2"] = Relation("inAppPurchases", item) }), id, new JArray(create));
        string lookup = "apple-" + key + "-pricepoints";
        var read = Add(files, requests, lookup, "Apple", "GET", Apple + "/v2/inAppPurchases/" + item + "/pricePoints?filter[territory]=" + o.appleBaseTerritory + "&limit=200",
            null, null, id, new JArray(create));
        read["select"] = new JObject { ["responseCollection"] = "data", ["attribute"] = "customerPrice", ["equalsDecimal"] = c.appleBasePrice,
            ["currency"] = c.appleBaseCurrency, ["territory"] = o.appleBaseTerritory, ["bindAs"] = lookup + ".selected.id",
            ["instructions"] = "Follow links.next until the exact price is found. Verify territory currency separately. Bind the matching data[].id; stop if no exact price exists." };
        read["selectionFile"] = folder + "00-pricepoint.json";
        var price = Resource("inAppPurchasePriceSchedules", null, new JObject { ["inAppPurchase"] = Relation("inAppPurchases", item),
            ["baseTerritory"] = Relation("territories", o.appleBaseTerritory),
            ["manualPrices"] = new JObject { ["data"] = new JArray(Identity("inAppPurchasePrices", "${price1}")) } });
        price["included"] = new JArray(new JObject { ["type"] = "inAppPurchasePrices", ["id"] = "${price1}",
            ["attributes"] = new JObject { ["startDate"] = null, ["endDate"] = null },
            ["relationships"] = new JObject { ["inAppPurchaseV2"] = Relation("inAppPurchases", item),
                ["inAppPurchasePricePoint"] = Relation("inAppPurchasePricePoints", "{{" + lookup + ".selected.id}}") } });
        Add(files, requests, "apple-" + key + "-pricing", "Apple", "POST", Apple + "/v1/inAppPurchasePriceSchedules", folder + "03-pricing.json",
            price, id, new JArray(create, lookup));
        Add(files, requests, "apple-" + key + "-availability", "Apple", "POST", Apple + "/v1/inAppPurchaseAvailabilities", folder + "04-availability.json",
            Resource("inAppPurchaseAvailabilities", new JObject { ["availableInNewTerritories"] = o.appleAvailableInNewTerritories },
                new JObject { ["inAppPurchase"] = Relation("inAppPurchases", item), ["availableTerritories"] = new JObject {
                    ["data"] = new JArray(o.appleAvailableTerritories.Select(t => Identity("territories", t))) } }), id, new JArray(create));
        string reserve = "apple-" + key + "-screenshot-reserve", upload = "apple-" + key + "-screenshot-upload";
        string screenshotId = "{{" + reserve + ".data.id}}";
        Add(files, requests, reserve, "Apple", "POST", Apple + "/v1/inAppPurchaseAppStoreReviewScreenshots", folder + "05-screenshot-reserve.json",
            Resource("inAppPurchaseAppStoreReviewScreenshots", new JObject { ["fileName"] = Path.GetFileName(image), ["fileSize"] = screenshot.LongLength },
                new JObject { ["inAppPurchaseV2"] = Relation("inAppPurchases", item) }), id, new JArray(create));
        requests.Add(new JObject { ["id"] = upload, ["platform"] = "Apple", ["productId"] = id,
            ["operation"] = "upload-returned-parts", ["dependsOn"] = new JArray(reserve), ["file"] = image,
            ["operationsFrom"] = reserve + ".data.attributes.uploadOperations",
            ["instructions"] = "For every returned upload operation, send the specified offset/length slice of the packaged image using that operation's method, URL and requestHeaders. Complete every part before committing. Use only the returned headers for these binary uploads." });
        string checksum;
        using (var md5 = MD5.Create()) checksum = BitConverter.ToString(md5.ComputeHash(screenshot)).Replace("-", "").ToLowerInvariant();
        var commit = Resource("inAppPurchaseAppStoreReviewScreenshots", new JObject { ["uploaded"] = true, ["sourceFileChecksum"] = checksum }, null);
        commit["data"]["id"] = screenshotId;
        ((JObject)commit["data"]).Remove("relationships");
        string complete = "apple-" + key + "-screenshot-commit";
        Add(files, requests, complete, "Apple", "PATCH", Apple + "/v1/inAppPurchaseAppStoreReviewScreenshots/" + screenshotId,
            folder + "06-screenshot-commit.json", commit, id, new JArray(reserve, upload));
        var verify = Add(files, requests, "apple-" + key + "-screenshot-verify", "Apple", "GET",
            Apple + "/v1/inAppPurchaseAppStoreReviewScreenshots/" + screenshotId, null, null, id, new JArray(complete));
        verify["instructions"] = "Poll data.attributes.assetDeliveryState.state until COMPLETE; stop and inspect errors if FAILED. Product review submission remains manual.";
    }

    public static string Export(JObject manifest, DlcStoreExportOptions options, string parentDirectory)
        => Export(manifest, options, parentDirectory, DlcAppleScreenshot.Prepare);

    public static string Export(JObject manifest, DlcStoreExportOptions options, string parentDirectory, Func<byte[], byte[]> prepareAppleScreenshot)
    {
        // Complete validation and serialization before touching the destination.
        var files = BuildPackage(manifest, options, prepareAppleScreenshot, out var screenshots);
        string directory = Path.Combine(Path.GetFullPath(parentDirectory), "dlc-store-export-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            string path = Path.Combine(directory, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(file.Value);
        }
        foreach (var image in screenshots)
        {
            string path = Path.Combine(directory, image.Key.Replace('/', Path.DirectorySeparatorChar));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(image.Value, 0, image.Value.Length);
        }
        return directory;
    }

    static byte[] ReadScreenshot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Select a PNG or JPEG file for this product.");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") throw new InvalidOperationException("Use a .png, .jpg or .jpeg file.");
        var bytes = File.ReadAllBytes(Path.GetFullPath(path));
        string actual = ScreenshotExtension(bytes);
        if ((extension == ".png") != (actual == ".png")) throw new InvalidOperationException("Image contents do not match the screenshot file extension.");
        return bytes;
    }

    static string ScreenshotExtension(byte[] bytes)
    {
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length > 8 && bytes.Take(8).SequenceEqual(png)) return ".png";
        if (bytes.Length > 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
        throw new InvalidOperationException("File is empty or does not have a PNG/JPEG image signature.");
    }

    static IEnumerable<JToken> Products(JObject manifest) => (manifest?["products"] as JArray)?.AsEnumerable() ?? Enumerable.Empty<JToken>();
    static bool Matches(string value, string pattern) => value != null && Regex.IsMatch(value, pattern);
    public static string StoreId(JToken product, string store) => (string)(product["storeIDs"] as JArray)?.FirstOrDefault(x => (string)x["store"] == store)?["id"] ?? (string)product["id"];
    static JObject Identity(string type, string id) => new JObject { ["type"] = type, ["id"] = id };
    static JObject Relation(string type, string id) => new JObject { ["data"] = Identity(type, id) };
    static JObject Resource(string type, JObject attributes, JObject relationships)
    {
        var data = new JObject { ["type"] = type, ["relationships"] = relationships };
        if (attributes != null) data["attributes"] = attributes;
        return new JObject { ["data"] = data };
    }
    static JObject Add(Dictionary<string, string> files, JArray requests, string key, string platform, string method, string url, string path, JObject body, string productId, JArray dependencies = null)
    {
        if (path != null) files.Add(path, body.ToString());
        var request = new JObject { ["id"] = key, ["platform"] = platform, ["method"] = method, ["url"] = url,
            ["dependsOn"] = dependencies ?? new JArray() };
        if (path != null) request["bodyFile"] = path;
        if (productId != null) request["productId"] = productId;
        requests.Add(request);
        return request;
    }
    static void CheckText(List<string> errors, string field, string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text.Contains("{{")) errors.Add(field + ": required, maximum " + limit + " characters; response-binding syntax {{ is reserved.");
    }
    static void CheckId(List<string> errors, string id, string value, string pattern, string platform)
    { if (!Matches(value, pattern)) errors.Add(id + ": invalid " + platform + " product ID: " + value); }
    static void CheckLocales(List<string> errors, string id, IEnumerable<DlcStoreExportLocalization> all, Func<DlcStoreExportLocalization, string> code, string platform, int titleMax, int descriptionMax, bool required = true)
    {
        var list = all.Where(l => l != null && !string.IsNullOrEmpty(code(l))).ToList();
        if (required && list.Count == 0) errors.Add(id + ": at least one " + platform + " localization is required.");
        if (list.Select(code).Distinct().Count() != list.Count) errors.Add(id + ": duplicate " + platform + " locales.");
        foreach (var l in list)
        {
            if (!Matches(code(l), LocalePattern)) errors.Add(id + ": invalid " + platform + " locale " + code(l));
            CheckText(errors, id + " " + platform + " " + code(l) + " title", l.title, titleMax);
            CheckText(errors, id + " " + platform + " " + code(l) + " description", l.description, descriptionMax);
        }
    }
    static void CheckPrice(List<string> errors, string field, string amount, string currency)
    {
        decimal parsed;
        if (!Matches(currency, @"^[A-Z]{3}$") || !Matches(amount, @"^(0|[1-9][0-9]{0,11})(\.[0-9]{1,9})?$") ||
            !decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
            errors.Add(field + ": positive decimal price and three-letter currency are required.");
    }
    static JObject Money(string amount, string currency)
    {
        decimal price = decimal.Parse(amount, CultureInfo.InvariantCulture), units = decimal.Truncate(price);
        return new JObject { ["currencyCode"] = currency, ["units"] = units.ToString(CultureInfo.InvariantCulture), ["nanos"] = (int)((price - units) * 1000000000m) };
    }

    public const string Instructions = @"# DLC Pump platform catalog export

This folder contains JSON request bodies, requests.json (ordered endpoints and dependencies), and validation.json. No request has been sent. It is not a dashboard-import format. Only non-consumable DLC creation is supported.

Use your own authenticated API client and submit bodies with Content-Type: application/json. Never add credentials to the Unity settings or this export. Keep API responses outside source control. Inspect the target account, existing IDs and every price before sending.

## PlayFab Economy v2
Use a title entity token in X-EntityToken with Catalog/CreateDraftItem. FriendlyId remains the Unity product ID; ContentType comes from the reviewed product settings. Creates catalogItem entries, including for DLC that the game grants as a local bundle. Does not create Economy bundles, virtual-currency prices, marketplace redemption mappings, entitlement code, or content uploads. Publish is false. Review and publish drafts manually in Game Manager after setup. Search existing FriendlyIds before submitting: creation is not an idempotent update.

## Apple App Store Connect
Use Authorization: Bearer with your App Store Connect JWT. Follow requests.json in order for each product. POST /v2/inAppPurchases first; save the returned data.id. Replace {{apple-NNNN-create.data.id}} in dependent JSON bodies AND URLs with that response ID (not the SKU).

GET the exported pricePoints URL, following links.next until the exact configured customer price is found in the base territory. Confirm the territory's currency (GET /v1/territories/{territory}). Bind the matching data[].id as {{apple-NNNN-pricepoints.selected.id}}. If no exact price exists, stop and correct settings before exporting again. The price point is product-specific and cannot be calculated from the legacy Unity applePriceTier.

Each Apple product folder contains 00-pricepoint.json with territory from Apple Base Territory and customerPrice from that product's Apple Base Price (preserved as a decimal string). This is local lookup metadata linked from the pricePoints request's selectionFile; it is not a request to create an Apple price point.

Then send localization, price schedule, and availability requests. Resolve all {{...}} response bindings before sending; these are response dependencies, not missing input fields. Keep Apple's inline ${price1} identifier unchanged. Apple automatically equalizes prices outside the base territory; review those prices in App Store Connect. Available territories are explicit; expansion is controlled by the exported flag. These are create requests: inspect existing products and do not blindly replay them. Tax/compliance setup, submission and approval remain manual.

Each Apple product includes its configured review screenshot resized to 1179 x 2556 portrait pixels as an opaque PNG in its own folder. The entire image is preserved with centered black padding where the aspect ratio differs; the source file is unchanged. POST 05-screenshot-reserve.json, substitute the returned screenshot data.id in the commit JSON and URL, and follow the upload-returned-parts operation in requests.json. Upload every specified byte range using the returned method, URL and requestHeaders, then PATCH 06-screenshot-commit.json. Its MD5 checksum and file size describe the packaged image; do not modify it. Poll the screenshot GET request until assetDeliveryState.state is COMPLETE, inspecting FAILED errors rather than submitting. Alternatively upload the packaged image manually under the product's App Review Information in App Store Connect. Local validation checks file readability and PNG/JPEG signatures; Apple still validates dimensions and review suitability. No screenshot is uploaded automatically.

## Google Play
Use Authorization: Bearer with an OAuth token scoped to https://www.googleapis.com/auth/androidpublisher. POST each exported batch (maximum 100 distinct products) to its requests.json URL. allowMissing=true enables creation; matching existing IDs will be updated. The update mask replaces listings and purchaseOptions: inspect existing products and merge any options/localizations that must be retained before submitting. The single buy option is legacy-compatible and disables multi-quantity. Listed regional prices are explicit; no new-region auto-expansion is requested. Review taxes/compliance and activate purchase options manually in Play Console after creation.

## References
- https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/create-draft-item?view=playfab-rest
- https://developer.apple.com/documentation/appstoreconnectapi/in-app-purchases
- https://developer.apple.com/documentation/appstoreconnectapi/uploading-assets-to-app-store-connect
- https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts/batchUpdate
- https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts
- https://developers.google.com/android-publisher/api-ref/rest/v3/RegionsVersion
";
}
