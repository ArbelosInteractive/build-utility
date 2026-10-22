# DLC Pump

Open **Build Utility > DLC Pump**. DLC Pump manages local DLC release definitions and build evidence. Its window uses the Arbelos water-pump icon.

## Project integration

Implement `IDlcProjectAdapter` in the consuming project's Editor assembly. Register an instance in an `[InitializeOnLoad]` static constructor using `DlcProject.Adapter`. The adapter supplies the project's display name, Unity IAP JSON catalog path, backup directory, unlockable catalog object, runtime product-to-asset mapping and content-discovery operation. The package has no dependency on a particular game's wrestler, arena, skin or progression classes.

The catalog adapter currently expects the Unity IAP product-catalog JSON schema. Product IDs and runtime ownership routes remain the consuming game's responsibility. An adapter may also implement `IDlcPackAuthoring` to expose **Create pack** for selected products; it must create both catalog entries and working runtime grants. The TSW adapter supports bundles of existing products and rejects nested bundles. New bundle definitions require a client update before publishing the store listing; they are not retroactively available to older clients.

## Author a release

1. Choose **New** and save outside Resources, preferably in Assets/Editor/DlcReleases.
2. Set release ID, minimum client version, target, Addressables profile and notes.
3. Select products. Edit listing titles/descriptions if needed; saves retain unknown JSON fields and create backups. Concurrent edits are rejected.
4. Discover content from project rules. Review required assets and Addressables keys. Discovery preserves manual assignments.
5. Validate and resolve errors. Save modified content assets before exporting an immutable manifest. Compare against the previous manifest.

## Build and inspect

Select the exact target and Addressables profile. A full build uses the shared checked build primitive. An update requires the original supported client's content-state file, archives that file and its hash, and never falls back to a full build. Both build configured Addressables groups, not only selected products. Build receipts record success or failure, manifest hash, provider and output path. Build success does not mean publication.

Use **Analyze Addressables build layout** with the layout JSON produced by that build. It reports bundle sizes including dependencies per product and duplicate asset GUIDs. These are empty-cache bundle-size estimates, not guaranteed network bytes. Shared/local bundles and the player's cache affect network usage. Build layout APIs must be available in the installed Addressables version; this integration is validated against TSW's installed version.

Record an artifact inventory outside the selected output directory. It contains each file's size and SHA-256. Verify a staging copy against it to detect missing, modified or unexpected files. Symlinks/junctions are rejected. Inventories are separate from build receipts and must be retained with the matching build.

## Store readiness and publishing

Export registration requirements, then compare a normalized store/backend snapshot with schemaVersion, environment, source, capturedUtc and products. Each product supplies id, type, playFabFriendlyId, contentType and available. This checks the supplied evidence, not a live purchase.

### Export platform API catalogs

In **Stores**, create or assign **Store Export Settings** outside Resources. Enable the desired platforms, then click **Add missing selected products to export settings**. This copies product reference names, text and payout subtypes without changing existing settings. It does not guess locales, prices or currencies. Validation and export use **every product in the export settings list**, independently of the selected release's product selection. The selected release only seeds new settings entries and supplies the release ID. Add products from other releases to the same settings asset to export them together. Removing a settings entry excludes that product. Each configured ID must resolve uniquely in the Unity IAP catalog; missing or incomplete entries block export. Only non-consumable products are supported; a content build is not required.

Complete the fields for each enabled platform:

- **PlayFab Economy v2:** hexadecimal title ID, reviewed product content type, and catalog title/description. The neutral strings come from the Unity catalog; optional translated strings use explicit PlayFab locale codes. FriendlyId remains the Unity product ID, which matches TSW's inventory and purchase lookup. Exported items are unpublished `catalogItem` drafts, including local DLC bundles. Prices and marketplace redemption are managed separately; this does not create Economy bundle references or rewrite game entitlements.
- **Apple App Store Connect:** numeric app ID, three-letter base and available territory IDs, reference name, localizations, explicit base price and currency, family sharing selection, and an **Apple Review Screenshot Path** for each product. Use **Browse** to select a PNG/JPEG; paths can be absolute or relative to the Unity project. New settings entries seed this path from the Unity catalog's screenshotPath when available. Existing settings paths are preserved. Localized names are limited to 30 characters and descriptions to 45. The base territory must be available. Prices in other available territories use Apple's automatic equalization. Legacy `applePriceTier` values are not treated as current price-point IDs.
- **Google Play:** package name, current supported regions version, localizations and a positive price/currency for each selected two-letter region. Titles are limited to 55 characters and descriptions to 200. Product creation includes one legacy-compatible buy option, with multi-quantity disabled. Other regions and future regions are not automatically enabled.

Click **Validate platform catalog export** to see all missing or invalid required inputs. **Export platform catalogs (JSON)** is blocked until those inputs are complete. It creates a uniquely named folder without overwriting prior exports, containing platform request bodies, `requests.json` with ordered endpoints/dependencies, `validation.json`, and manual submission instructions. `AppleAppStore` and `GooglePlay` catalog ID overrides are respected; duplicate effective IDs are rejected. These are API payloads, not dashboard import files.

Apple assigns resource IDs only after product creation. The export therefore documents explicit `{{request-id.data.id}}` response bindings in dependent payloads/URLs. Its price lookup specifies the entered amount/currency/base territory; the operator selects the exact product-specific returned price-point ID, following pagination, before submitting the price schedule. Those bindings must be resolved before submission. Apple's inline `${price1}` identifier must be retained unchanged. Missing response IDs are documented dependencies, not missing configuration inputs.

Each Apple product folder also contains `review-screenshot.png` or `.jpg`, `05-screenshot-reserve.json` and `06-screenshot-commit.json`. The image is captured once during export so its copied bytes, reservation size and MD5 commit checksum agree. Missing, unreadable, empty or incorrectly signed PNG/JPEG files block Apple export before output is written; non-Apple exports do not require a screenshot. Local checks do not certify Apple's dimension or review-content requirements. In `requests.json`, reserve the screenshot, upload every returned uploadOperations byte range with its specified method/URL/headers, commit using the returned screenshot ID, then poll processing until COMPLETE. The binary upload operation is a documented manual step, not a precomputed HTTP request. Alternatively upload the packaged image in App Store Connect's product App Review Information. No screenshot is uploaded automatically.

Submit with your own API client: PlayFab title entity token, App Store Connect JWT, or Google Android Publisher OAuth token. No credentials are stored and no API is called by this feature. Inspect existing IDs before submission: PlayFab and Apple requests create products; Google uses `allowMissing=true` and can update existing products, replacing their listings and purchase options within the export's update mask. Preserve any existing data that must remain before manually submitting. Publication, Google purchase-option activation, store screenshots, tax/compliance setup, reviews and live purchase verification remain manual.

Schema references: [PlayFab CreateDraftItem](https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/create-draft-item?view=playfab-rest), [Apple In-App Purchases](https://developer.apple.com/documentation/appstoreconnectapi/in-app-purchases), [Google batchUpdate](https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts/batchUpdate), [Google product schema](https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts), and [Google regions version](https://developers.google.com/android-publisher/api-ref/rest/v3/RegionsVersion).

The existing **Seamless Build** tools remain accessible for Azure/CRC workflows. DLC Pump does not automatically publish, promote, or roll back a live catalog. Keep the previous known-good catalog, referenced bundles and content-state baselines. Verify staging purchase, restore, account switching, interrupted downloads and supported old clients before publishing.

## Validation

## Publish to staging

In **Publish**, create a publishing settings asset outside Resources. Its public Inspector fields configure the Azure endpoint, account, container, release prefix, environment, and PlayFab title/environment. PlayFab fields describe the intended destination; they do not register products or verify purchases automatically.

Select the matching manifest, successful build receipt, artifact inventory and output folder. **Upload and verify** publishes into a release-specific prefix, then downloads every file and checks its SHA-256. Existing blobs are never overwritten. **Verify remote** repeats the download verification. Save verification receipts outside the output folder. Store publication remains a separate step.

The publisher requires Node.js and `@azure/storage-blob` installed in the configured dependencies directory. Cloud account keys are read from the named environment variable, never stored in Unity assets. Addressables remote load paths must match the intended published URL before building.

TSW includes a free local emulator setup under `Tools/DlcStaging`. Run `npm ci --prefix Tools/DlcStaging`, then `python Tools/DlcStaging/start-emulator.py` from the project root. Default publishing settings point at this loopback-only emulator. It uses a disposable development key and needs no cloud account. This is development storage, not public hosting for players. Emulator data persists under that directory until removed; restart the script after reboot.

## Test coverage

Package EditMode tests are in Tests/Editor. TSW's adapter also provides an editor integration test for real content discovery, serialized references and Undo/Redo. Source compilation and pure regression tests are separate from a live Addressables build or store verification.
