# Changelog

## [1.0.60] - 2026-11-03

- Add DLC content export: one product.json per product, listing each product's real Addressables addresses (content entries with a blank addressablesKey are local content and ignored; products with no keys are still published with an empty key list, so products.json lists everything in the store for that app version), validated against the Addressables settings and the built remote catalog, with a dry run.
- Add Tools~/DlcPublisher/publish_dlc.py: publishes bundles, the per-app-version catalog, immutable v<N>/product.json and products.json (ETag If-Match) to <Platform>/<appVersion>/, signed in as the Azure CLI user; dry run, product subset, rollback and status, with a log per run. It refuses a full rebuild of a published app version (a catalog with a different name) unless --allow-new-catalog is given.
- Add runtime DlcContentService and DlcRemoteConfig: LoadAllAsync checks products.json with the server on every call and loads every published product's manifest (ownership stays in game code); product.json download with ETag caching and offline fallback, minimum client version check, key check against the loaded catalog with one catalog update retry.
- Add the Build Utility > DLC Publish window: choose publishing settings and a releases folder, list and check releases, validate/export, publish (dry run, checked products only), status and rollback, plus Sign in to Azure / Check again / Sign out (Azure CLI; optional azureTenant) with a container access check (`publish_dlc.py check`).
- DlcPublishingSettings now holds only the account, container, CDN base URL, access mode, staging folder and Python path. Documented in Documentation~/DLC-CONTENT.md.
- Add the Build Utility > DLC Store Export window (store export settings and releases folder chosen in the window, add missing products from checked releases, validate, export). The DLC Pump Stores tab keeps store-readiness checks and links to it.
- DLC Publish shows a progress bar while publishing (step, MB uploaded, current file), streams the output live and can cancel. A failed or cancelled publish can be continued with **Resume publish**: publish_dlc.py retries failed uploads, keeps uploading the other bundles when one fails (then stops before the catalog), and on a rerun reuses a v<N>/product.json left by the interrupted run instead of taking the next number. The 30-minute limit is replaced by a 10-minute no-output limit.
- DlcRelease no longer has a build target: every release ships to both Android and iOS. Build, export and publish use the editor's active build target, which must be Android or iOS, and the release list no longer greys out releases by platform. product.json no longer has a "target" field ("platform" already says which platform it was exported for).
- Store the content-state baseline in Azure at _build-state/<Platform>/<appVersion>/addressables_content_state.bin. Export includes the local file only when its playerVersion matches the exported catalog; publish uploads it once (same file skipped, a different one fails before anything is written unless --allow-new-catalog). New `publish_dlc.py baseline` command and a **Download from Azure** button, which fills in the content-state baseline field.
- Move building from the DLC Pump window's Build tab into DLC Publish, as a Build section above Export: **Full build** and **Content update** buttons for the checked releases (a build covers all Addressables groups for the active platform and app version), with a suggestion read from Azure (`publish_dlc.py status --state-json`) and a confirmation when building against it. Receipts are saved to Logs/dlc-builds/<Platform>/<appVersion>/ instead of a folder picked each time. A full build that reports an error now fails. Build layout analysis and artifact inventory tools moved to a collapsed **Build tools** section.
- Add an **Upload** section to DLC Store Export, replacing the IAP Uploader's Tkinter window: Key Vault name, tenant and Python path in the settings asset, Azure sign-in and vault check, the export picked automatically after exporting, per-item tick boxes and live statuses, **Dry run** / **Upload…** per store plus **Upload all checked…**, Google activate and PlayFab publish options, Cancel (kills the process tree) and **Install Python packages**. `upload_all.py` gains `--export`, `--vault`, `--log-dir`, `--list`, `--check-vault` and `--progress`; `iap_items.py` holds the item listing; `iap_common.Report` emits live `@@item` lines. Python tools now share one runner (`DlcPythonProcess`).
- PlayFab release waves in DLC Store Export: pick a wave schedule and tick waves; exported PlayFab items of those waves get StartDate, EndDate, IsHidden and dlcPump metadata (via DlcPlayFabPlan.Apply), and release-waves.json is saved with the export. Validation: overlapping release windows for a product, a product in more than one selected release wave, and wave products missing from the export.
- Release waves: a product may be in several release waves when their windows don't overlap (was: one release window per product). DlcWaveExport.Build takes the selected wave IDs; without them (Review PlayFab) each product gets its active, else next, else last window. The Release Waves window shows problems live and has a calendar date/time picker (**Pick…**) next to each date field.
- IAP uploader: updating an existing PlayFab item now merges with the draft (keeps images, dates, hidden flag, tags and other display properties the file leaves out, and sends the draft's ETag). Before, an update replaced the item and cleared those fields, including thumbnails uploaded by the Review window.
- Icons moved out of Release Waves: the window no longer has the Icon Studio button, product thumbnails or per-product Icon buttons. The DLC Pump window's Products tab shows each release product's icon with an **Edit icon** button. Review PlayFab opened from Release Waves starts without an icon, and from Icon Studio without a schedule.
- Staging and production DLC content: DlcPublishingSettings has a staging `container` (default dlc-staging) and a `productionContainer` (dlc-production); `cdnBaseUrl` moved out of the publishing settings. DLC Publish publishes to staging and has a **Promote to production** section (dry run, checked products only, production status); Rollback has a Container choice. New `publish_dlc.py promote` copies a version's bundles, catalog and product manifests unchanged (server-side copy with a download/upload fallback), then writes production's products.json.
- Runtime: DlcEnvironment chooses the container. DlcRemoteConfig has `cdnBaseUrl` (production) and `stagingCdnBaseUrl`; `DlcEnvironment.UseStaging(true)` switches to staging before Addressables initializes, and Remote.LoadPath reads the URL at runtime (`{Arbelos.BuildUtility.Runtime.DlcEnvironment.ContentUrl}/[BuildTarget]/[UnityEngine.Application.version]`), so promoted files work unchanged. DLC Publish warns about a fixed Remote.LoadPath and can switch the active profile to the runtime URL; the export checks DlcRemoteConfig.
- PlayFab staging title: DLC Store Export has a **PlayFab title** choice (Staging by default, resets on reload); `upload_all.py --playfab-target staging|production` and `upload_playfab_items.py --target`. Staging uploads are recorded in playfab-staging-receipt.json (new `iap_staging.py`); production refuses items whose file wasn't uploaded to staging as it is now, unless `--skip-staging-check`. The vault check reports both title IDs; items show their staging state.
- DLC PlayFab Review: a connection can read its secret from Key Vault (`keyVaultName`, `keyVaultSecretName`) as the signed-in Azure CLI user instead of an environment variable.
- Full build / Content update no longer freeze the Editor without feedback: releases are validated first (all problems listed in the report, nothing built), fingerprinting shows a cancelable progress bar, and file hashes are cached by size and modified time, so the second fingerprint before the build and later builds only hash changed files.
### Removed
- The IAP Uploader's Tkinter window (`upload_gui.py`) and `Open IAP Uploader.bat`; use DLC Store Export > Upload.
- The DLC Pump Publish tab, DlcPublisher and Tools~/DlcPublisher/publish.mjs (Node upload with a storage key), DlcRelease.publishingSettings and DlcRelease.storeExportSettings.

## [1.0.59] - 2026-10-30

- Accept PlayFab's published FriendlyId projection without treating it as an unrelated draft edit.
- Compare equivalent UTC date values and absent end dates consistently during review, repeat publication and verification.
- Match signed draft image URLs to their published PlayFab CDN content identity while still rejecting changed images.
- Verify staging icon publication, duplicate-upload avoidance, pause and resume against the live staging title; add focused regressions.

## [1.0.58] - 2026-10-30

- Add an explicit PlayFab review/publish panel for availability waves, promotions and Icon Studio thumbnails on existing Economy v2 items.
- Protect concurrent edits with ETags, reject unrelated pending drafts, preserve prices/localizations/detail artwork, and verify published fields with per-item receipts.
- Keep title credentials in an external environment variable and an in-memory token; no automatic live requests or marketplace activation.
- Document deployment prerequisites and the updated TSW client's independent inventory resolution, refreshed offer checks and asynchronous remote artwork fallback.
- Add mocked publishing regressions covering conflicts, cancellation, repeat publication, image preservation and read-back failures.

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Released]

## [1.0.57] - 2026-10-30
### Added
- Dockable Release Waves workspace: product shelf, drag/drop and multi-selection, saved schedule assets, time-zone/DST validation, local date preview, cadence copies, and deterministic JSON export. Export remains local; it does not schedule live services.
- Icon Studio: original category templates, layered pixel documents, pencil/eraser/fill/eyedropper/line/rectangle, selection and move, mirror, grid/zoom, import, Undo/Redo, store-size previews, and transparent PNG export.
- Optional IDlcIconAuthoring adapter for project-specific local icon assignment, with backups before replacement. Editable icon documents remain separate from exported PNGs and Apple review screenshots.
- Artist-tool model, image, serialization/Undo, and native window construction tests, plus in-editor Help and usage instructions.

## [1.0.56] - 2026-10-25
### Added
- Per-product Apple 00-pricepoint.json with the configured base territory and customer price, linked from the price-point lookup request.
### Changed
- Exported Apple review screenshots are resized to 1179 x 2556 portrait PNGs, preserving the whole image with black padding and leaving source files unchanged. Upload sizes and checksums use the resized bytes.

## [1.0.55] - 2026-10-22
### Added
- Per-product Apple review screenshot path with a PNG/JPEG file picker. Apple export requires readable screenshot files and copies each image into its product export folder.
- Apple screenshot upload reservation and MD5 completion payloads, response-driven binary upload instructions and processing verification in the request manifest. Submission remains manual.

## [1.0.54] - 2026-10-22
### Fixed
- Platform catalog validation and export now include every product listed in Store Export Settings, independently of the selected release's products. The release selection only seeds the settings list.
- Unknown, duplicate, empty or incomplete configured products block export instead of being silently omitted. The Stores tab shows the configured export count.

## [1.0.53] - 2026-10-21
### Added
- DLC Pump Stores-tab export settings and validated JSON payloads for PlayFab Economy v2 drafts, Apple non-consumable products/localizations/pricing/availability, and Google Play one-time product batches of up to 100.
- Ordered API request manifest, Apple response/price-point bindings, validation report and manual submission instructions. No API calls or credentials are required to export.
- Regression coverage for required inputs, store ID overrides, duplicate IDs/locales, pricing precision, batch splitting, Apple dependencies and immutable export directories.
### Fixed
- Package EditMode tests explicitly reference Newtonsoft.Json.dll so they compile when enabled in a consuming project's testables.

## [1.0.52] - 2026-10-17
### Added
- DLC Pump release workspace with project adapter, catalog backups, asset validation, immutable manifests, full/update receipts, artifact inventories, store snapshot comparison and build-layout analysis.
- Shared checked full-build method that returns the Addressables result.
- DLC Pump instructions and EditMode regression tests.
### Fixed
- Azure uploads now fail on missing configuration or failed HTTP requests instead of reporting completion; requests are disposed and have a timeout.

## [1.0.0] - 2024-06-15
### Added
- Initial release

## [1.0.1] - 2024-06-19
### Added
- Fixed WebSocketSharp Errors

## [1.0.2] - 2024-06-19
### Added
- Added Guide PDF.

## [1.0.3] - 2024-06-19
### Added
- Added Guide PDF meta.

## [1.0.4] - 2024-06-19
### Added
- Removed VisionOS Support

## [1.0.5] - 2024-06-20
### Added
- Auto-Setup Custom Addressable Build Asset assign and auto-setup built in player group settings.
- Update Addressables Dependency Version.

- ## [1.0.6] - 2024-06-21
### Added
- Add support for unity built in shaders bundles generated by addressables for projects.
- Fix manual file number checks for a specific project.

- ## [1.0.7] - 2024-07-03
### Added
- Runtime Assembly Support for "Any Platform"

- ## [1.0.8] - 2024-07-03
### Added
- Unity versions below v2022 won't have PlayerSettings.insecureHttpOption.

- ## [1.0.9] - 2024-07-04
### Added
- Add WebGL azure friendly build target case.

- ## [1.0.10] - 2024-07-04
### Added
- Remove azure shared key saving and input taken when building.

- ## [1.0.11] - 2024-07-09
### Added
- Allow Http Web Request option in build tool and player pref saving.

- ## [1.0.12] - 2024-07-09
### Added
- Fix Asset File Id Fetcher hard coded count 2.

- ## [1.0.13] - 2024-09-23
### Updated
- Updated Addressable Profile Check to make it less restrictive and consider any profile created.
- Removed unnecessary debugs.

- ## [1.0.14] - 2024-09-28
### Updated
- Add custom function to switch addressable profiles by name.
- Add functionality to pass custom addressable build path when calling GenerateCRCValues function.

- ## [1.0.15] - 2024-09-28
### Updated
- Add custom function to load custom content catalog using a remote path string.

- ## [1.0.16] - 2024-09-29
### Updated
- UI Buttons to Only Build Addressables or Build and Upload Addressables.

- ## [1.0.17] - 2024-10-04
### Updated
- Update SwitchAddressableProfile by name to load all resources.

- ## [1.0.18] - 2024-10-04
### Updated
- Add missing using directive.

- ## [1.0.19] - 2024-10-04
### Updated
- Add cloud build functions using LoadAssetAtPath.

- ## [1.0.20] - 2024-10-05
### Updated
- No addressable build path found helper log.

- ## [1.0.21] - 2025-04-21
### Updated
- Addressables Downloader - Percentage Updated Unity Event Addition.

- ## [1.0.22] - 2025-04-23
### Updated
- Addressables Downloader - Improved Download Logic where interruptions are accounted for and downloads are resumed in that case.

- ## [1.0.23] - 2025-04-24
### Fixed
- Addressables Downloader - Fixed DownloadKeysAsync to use a new list to iterate through and modify pending and downloaded key list inside the iteration

- ## [1.0.24] - 2025-04-25
### Fixed
- Addressables Downloader - Convert into array to use as new iteration instead of list.

- ## [1.0.25] - 2025-04-25
### Fixed
- Addressables Downloader - Fix percentage downloaded event not being invoked on asset download.

- ## [1.0.26] - 2025-04-25
### Fixed
- Addressables Downloader - While Loop fix

- ## [1.0.27] - 2025-04-28
### Fixed
- Addressables Downloader - Catch errors

- ## [1.0.28] - 2025-04-29
### Fixed
- Addressables Downloader - Working download resume.

- ## [1.0.29] - 2025-05-02
### Fixed
- Addressables Downloader - Fix multiple download calls being cancelled and add a 3 sec delay to resume download.

- ## [1.0.30] - 2025-05-04
### Fixed
- Addressables Downloader - Proper release of addressables async operation handles.

- ## [1.0.31] - 2025-05-12
### Fixed
- Addressables Downloader - Update download coroutine handling and set result if stopped.
- Remove unused function.

- ## [1.0.32] - 2025-05-12
### Fixed
- Addressables Downloader - Lots of addressables asset validation logs.

- ## [1.0.33] - 2025-05-14
### Fixed
- Addressables Downloader - Updated Addressables - Clear Previous Catalog Function to use current addressables data to get latest catalog file.

- ## [1.0.34] - 2025-05-16
### Fixed
- Addressables Downloader - Clear Previous Catalogs - Prevent current catalog files from being deleted.

- ## [1.0.35] - 2025-05-16
### Fixed
- Addressables Downloader - Re-download invalid game files.

- ## [1.0.36] - 2025-05-23
### Fixed
- Addressables Downloader - Fix re-download invalid files and skip download size check.

- ## [1.0.37] - 2025-05-23
### Fixed
- Addressables Downloader - Skip size check only for built in shaders - redo them again.
-
- ## [1.0.38] - 2025-05-23
### Fixed
- Addressables Downloader - Revert redownload invalid key logic

- ## [1.0.39] - 2025-05-29
### Fixed
- Addressables Downloader - Change back to async function

- ## [1.0.40] - 2025-05-29
### Fixed
- Addressables Downloader - make the game file validation, more strict.

- ## [1.0.41] - 2025-05-29
### Fixed
- Addressables Downloader - A skip asset list to optionally skip a few things and it's check.

- ## [1.0.42] - 2025-06-06
### Updated
- Custom Addressable Tools and Custom Build Tools scripts: added support for making incremental addressable updates.
- Also increased the build tool window size to fit all the expanded content.

- ## [1.0.43] - 2025-06-13
### Updated
- Skip purging addressables files before downloading new ones
- Add support cloud functions to update previous addressable build (incremental)

- ## [1.0.44] - 2025-06-13
### Updated
- Fix async script error

- ## [1.0.45] - 2025-07-02
### Updated
- Comment out local hosting for unity 2023 or newer to fix unity 6.0 errors

- ## [1.0.46] - 2025-10-10
### Updated
- Fix Unity 6.0 - Changed Naming Convention Errors.

- ## [1.0.47] - 2025-10-27
### Updated
- Fix Unity 6.0 - Added support for .bin catalog files post addressables package update in unity 6.

- ## [1.0.48] - 2025-11-06
### Updated
- Addressables Downloader - Updated LoadCustomContentCatalog() method to return IResourceLocator created during content loading

- ## [1.0.49] - 2025-12-02
### Updated
- Addressables Downloader - Update addressables downloader flow, skip catalog validations, and support for more custom bundle naming conventions.

- ## [1.0.50] - 2025-12-03
### Updated
- Addressables Downloader - Update addressables downloader flow to keep record of FTUE Downloads and skip download repetitions.

- ## [1.0.51] - 2025-12-03
### Updated
- Addressables Downloader - Added Start and Complete Key Logs.
