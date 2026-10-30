# DLC Pump

Open **Build Utility > DLC Pump**. DLC Pump manages local DLC release definitions and build evidence. Its window uses the Arbelos water-pump icon.

## Artist workspace (1.0.58)

Open **Release Waves** or **Icon Studio** from the DLC Pump header. They are independent dockable editor windows; no content build or selected release is required. Both have **Help** buttons. Drafts stay local until you use **Review PlayFab** and explicitly publish. The updated TSW client resolves owned inventory independently of visible offers. Apple/Google availability remains a separate marketplace task.

### Release Waves

1. Choose **New schedule** and save the asset outside Resources. Use **New wave** to create a named group.
2. Enter start and optional end in `YYYY-MM-DD HH:mm`. Blank end means stays available. Choose a time zone with **Choose zone**; exported timestamps are UTC. Skipped/repeated daylight-saving times are rejected.
3. Drag products from the product shelf into the selected wave. Alternatively select checkboxes and **Add selected to wave**, or use each product's **+**. Project-window drops accept `DlcRelease` assets and add their known product IDs; arbitrary art assets are rejected. Search matches ID or title.
4. **Feature existing products only** marks a promotion rather than a release. **Paused in exported plan** is a draft schedule flag, not an immediate server action. Repeat promotion membership is permitted; more than one release window per product blocks export, including paused windows.
5. **Next week** / **Next fortnight** copies dates into a new wave with a fresh ID and no products. Populate it deliberately; existing waves are unchanged. Remove products or delete waves with Undo support.
6. Enter a **Preview local date** to view wave status at that instant, or leave it blank for now. This is a local simulation, not a live storefront query. Validate before exporting.
7. **Save drafts** preserves project assets. **Export JSON** writes `release-waves.json`, a versioned DLC Pump schedule document, not a direct PlayFab request. It contains stable schedule/wave IDs, purpose, pause state, UTC windows and product IDs. Unassigned products remain unchanged. Deploy the updated ownership-safe game client before publishing availability dates.

### Icon Studio

1. Use **Icon** beside a product in Release Waves. An existing matching icon document is opened when available; otherwise choose a size and **New**. Save editable documents outside Resources. You can also open Icon Studio directly and enter a catalog product ID.
2. Pixel presets are 32/64/128; 256/512 can be used for headshots. The size selector applies to new documents. Existing source pixels are never silently resized. Enable **Smooth headshot** before importing photos; pixel artwork uses nearest-neighbor sampling.
3. The original default library includes arena/ring, wrestler silhouette, outfit, taunt, prop, bundle, frame and badge templates. Each adds an independent layer in the current paint color. Import PNG/JPEG as a centered, aspect-preserving layer. Originals are untouched.
4. Choose a layer and tool. Pencil/eraser, fill, eyedropper, line/rectangle, mirror, grid, zoom and palette are available. Drag **Select**, then **Move** to relocate the selected pixels; pixels moved outside the canvas are clipped. Layers can be renamed, hidden, reordered, added or deleted. Undo/Redo applies to document edits and strokes.
5. Inspect the 64/128-pixel previews. **Save draft** preserves layers and product association. **Export PNG** writes the composited transparent image at source size.
6. **Assign to product** requires a project adapter implementing `IDlcIconAuthoring.IconAssetPath(productId)`. It validates the catalog ID, saves/imports the PNG at the adapter's path, configures alpha and sampling, and records the assignment. Existing files require replacement confirmation and receive a timestamped backup in the adapter's backup directory. Pixel Undo does not revert exported files; restore a backup if needed.
7. In TSW the adapter targets `Assets/Resources/StoreIcons/{productId}.png` for the local fallback. Restart Play mode after replacing that bundled file. Use **Review PlayFab upload…** to publish a remote thumbnail separately; the updated game loads remote artwork asynchronously.

Store icons, detail artwork, and Apple review screenshots are separate assets. Icon Studio does not replace the existing 1179 x 2556 Apple screenshot export. PlayFab thumbnail delivery and availability publishing are described below. Rendered detail images and recurring whole-store rotations remain separate work.

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

Each Apple product folder also contains `00-pricepoint.json`, `review-screenshot.png`, `05-screenshot-reserve.json` and `06-screenshot-commit.json`. The price-point file contains `territory` from Apple Base Territory and `customerPrice` from the product Apple Base Price, as a decimal string; the price-point lookup in requests.json references it through `selectionFile`. Each source screenshot is resized once per export to **1179 x 2556 portrait**, preserving its aspect ratio with centered black padding and flattening transparency onto black. The original is unchanged. The exported PNG bytes, reservation size and MD5 commit checksum agree. Missing, unreadable, empty or incorrectly signed PNG/JPEG files block Apple export before output is written; non-Apple exports do not require a screenshot. The requested output dimensions are enforced locally; review-content suitability still needs checking. In `requests.json`, reserve the screenshot, upload every returned uploadOperations byte range with its specified method/URL/headers, commit using the returned screenshot ID, then poll processing until COMPLETE. The binary upload operation is a documented manual step, not a precomputed HTTP request. Alternatively upload the packaged image in App Store Connect's product App Review Information. No screenshot is uploaded automatically.

Submit with your own API client: PlayFab title entity token, App Store Connect JWT, or Google Android Publisher OAuth token. No credentials are stored and no API is called by this feature. Inspect existing IDs before submission: PlayFab and Apple requests create products; Google uses `allowMissing=true` and can update existing products, replacing their listings and purchase options within the export's update mask. Preserve any existing data that must remain before manually submitting. Publication, Google purchase-option activation, store screenshots, tax/compliance setup, reviews and live purchase verification remain manual.

Schema references: [PlayFab CreateDraftItem](https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/create-draft-item?view=playfab-rest), [Apple In-App Purchases](https://developer.apple.com/documentation/appstoreconnectapi/in-app-purchases), [Google batchUpdate](https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts/batchUpdate), [Google product schema](https://developers.google.com/android-publisher/api-ref/rest/v3/monetization.onetimeproducts), and [Google regions version](https://developers.google.com/android-publisher/api-ref/rest/v3/RegionsVersion).

The existing **Seamless Build** tools remain accessible for Azure/CRC workflows. DLC Pump does not automatically publish, promote, or roll back a live catalog. Keep the previous known-good catalog, referenced bundles and content-state baselines. Verify staging purchase, restore, account switching, interrupted downloads and supported old clients before publishing.


### Connect, review and publish to PlayFab

1. Open **Review PlayFab…** in Release Waves or **Review PlayFab upload…** in Icon Studio. The panel shows the selected schedule and icon; clear either optional field to exclude it. You can also use **Build Utility > DLC PlayFab Review**.
2. Choose **New connection**, save outside Resources, and give it a clear environment name such as Test. Enter the PlayFab **title ID**. Connection assets contain configuration only.
3. A developer sets the title secret in the operating system environment variable named by **Secret Environment Variable** (default `DLC_PUMP_PLAYFAB_SECRET`), then restarts Unity so it inherits that variable. Enter the variable name in the panel, never the secret. Keep secrets out of source control, screenshots and receipts. The tool exchanges it for an in-memory title entity token.
4. For availability waves, deploy this updated client to all supported players before checking **Ownership Safe Client Deployed**. This checkbox is an operator confirmation, not a deployment detector; it cannot protect older clients. Start with a test title and verify purchase/restore/account switching there.
5. Click **Read & review PlayFab changes**. This authenticates and reads only. Existing published Economy v2 items must have `FriendlyId` matching the Unity product ID. Missing items, unrelated unpublished drafts, conflicting schedule ownership, or oversized display properties block review. Item creation remains the platform-catalog export workflow.
6. Inspect the environment, title, product IDs, old/new dates, visibility, promotions and thumbnail hash. Existing prices, localizations and other artwork are preserved. Changing local inputs invalidates the review.
7. Choose **Publish reviewed changes…**, confirm the displayed destination, and choose a JSON receipt location. The tool checks each item's ETag again, uploads any changed thumbnail, updates and publishes the draft, polls publish status, and reads the published item back using `GetItems`. An item is marked verified only when the expected fields match. Publication is per item, not an all-or-nothing transaction.
8. A failure or cancellation may leave a submitted request running remotely. Inspect PlayFab and the receipt, resolve pending drafts there, then review again. The tool stops at the first failure and does not automatically retry writes. Previously verified items remain published. Do not blindly replay a failed receipt.

A release wave sets the catalog start date, optional end date and hidden flag (paused means hidden). Dates are stored remotely, so Unity need not remain open. A promotion writes scheduling metadata without changing purchase dates or visibility; the updated store sorts currently featured offers first. A product can have one availability window and multiple promotions. Removing a product from the local schedule does not automatically undo its remote dates: explicitly review the desired dates/visibility before removing it. Switching schedule ownership also requires deliberate remote reconciliation.

This controls in-game offer discovery and the game's purchase preflight. It does not activate or deactivate Apple/Google products, schedule marketplace promotions, or add a server-side purchase authorization boundary. Existing receipt validation and granting remain unchanged, including delayed valid receipts. Store products must still be configured and approved in their marketplaces.

### What the updated game does

- **Owned inventory:** inventory IDs and amounts are resolved independently of shop search. Hidden or expired products retain owned entries. Missing metadata retains the inventory ID; cached metadata supplies labels and mappings, never ownership. Same-account authoritative inventory remains visible during a failed refresh. A fresh login still needs authenticated inventory; this is not a new offline entitlement system.
- **Offer visibility:** unowned non-consumables need an available PlayFab item mapped by FriendlyId; unmapped items are withheld. Consumables retain their existing flow. Available offers refresh while the store is open (about every minute and on resume), expire locally at their end time, and become unavailable for purchase if the last successful refresh is older than three minutes. Non-consumable purchases also perform a fresh lookup before initiating marketplace checkout.
- **Remote artwork:** the first PlayFab Thumbnail supplies the icon; the first Screenshot supplies the item-detail image. HTTPS PNGs up to 4 MiB and 2048 x 2048 pixels are downloaded asynchronously. Local Resources icons remain the fallback. The disk cache retains at most 64 PNG files; account changes reset the in-memory selection. Unchanged URLs reuse cached images, so publish a new image URL to update artwork. Missing or unsupported remote artwork does not block the store.
- **Pixel sampling:** Icon Studio publishes its pixel-art flag with the thumbnail. Pixel icons use point sampling; smooth headshots use bilinear sampling. Upload replaces only the Thumbnail and preserves existing Screenshot images. Icon Studio does not render or upload a separate description image in this version.

API references: [catalog ETags](https://learn.microsoft.com/en-us/xbox/playfab/economy-monetization/economy-v2/catalog/etags), [PublishDraftItem](https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/publish-draft-item?view=playfab-rest), [GetItems](https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/get-items?view=playfab-rest), [CreateUploadUrls](https://learn.microsoft.com/en-us/rest/api/playfab/economy/catalog/create-upload-urls?view=playfab-rest).

## Validation

## Publish to staging

In **Publish**, create a publishing settings asset outside Resources. Its public Inspector fields configure the Azure endpoint, account, container, release prefix, environment, and PlayFab title/environment. PlayFab fields describe the intended destination; they do not register products or verify purchases automatically.

Select the matching manifest, successful build receipt, artifact inventory and output folder. **Upload and verify** publishes into a release-specific prefix, then downloads every file and checks its SHA-256. Existing blobs are never overwritten. **Verify remote** repeats the download verification. Save verification receipts outside the output folder. Store publication remains a separate step.

The publisher requires Node.js and `@azure/storage-blob` installed in the configured dependencies directory. Cloud account keys are read from the named environment variable, never stored in Unity assets. Addressables remote load paths must match the intended published URL before building.

TSW includes a free local emulator setup under `Tools/DlcStaging`. Run `npm ci --prefix Tools/DlcStaging`, then `python Tools/DlcStaging/start-emulator.py` from the project root. Default publishing settings point at this loopback-only emulator. It uses a disposable development key and needs no cloud account. This is development storage, not public hosting for players. Emulator data persists under that directory until removed; restart the script after reboot.

## Test coverage

Package EditMode tests are in Tests/Editor. TSW's adapter also provides an editor integration test for real content discovery, serialized references and Undo/Redo. Source compilation and pure regression tests are separate from a live Addressables build or store verification.

The TSW checkout includes `Tools/StoryAudit/validate-dlc-liveops.py --unity <Unity.exe>` for offline publishing and inventory-rule tests. It creates an isolated project under Library, uses the installed PlayFab SDK and the production inventory DTO, and calls no live APIs. The full artist/export suite additionally covers native image output, serialization, Undo and panel construction. These checks do not replace staging purchase, restore, account-switching or remote-network testing.
