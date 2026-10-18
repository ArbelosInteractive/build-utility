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

The existing **Seamless Build** tools remain accessible for Azure/CRC workflows. DLC Pump does not automatically publish, promote, or roll back a live catalog. Keep the previous known-good catalog, referenced bundles and content-state baselines. Verify staging purchase, restore, account switching, interrupted downloads and supported old clients before publishing.

## Validation

## Publish to staging

In **Publish**, create a publishing settings asset outside Resources. Its public Inspector fields configure the Azure endpoint, account, container, release prefix, environment, and PlayFab title/environment. PlayFab fields describe the intended destination; they do not register products or verify purchases automatically.

Select the matching manifest, successful build receipt, artifact inventory and output folder. **Upload and verify** publishes into a release-specific prefix, then downloads every file and checks its SHA-256. Existing blobs are never overwritten. **Verify remote** repeats the download verification. Save verification receipts outside the output folder. Store publication remains a separate step.

The publisher requires Node.js and `@azure/storage-blob` installed in the configured dependencies directory. Cloud account keys are read from the named environment variable, never stored in Unity assets. Addressables remote load paths must match the intended published URL before building.

TSW includes a free local emulator setup under `Tools/DlcStaging`. Run `npm ci --prefix Tools/DlcStaging`, then `python Tools/DlcStaging/start-emulator.py` from the project root. Default publishing settings point at this loopback-only emulator. It uses a disposable development key and needs no cloud account. This is development storage, not public hosting for players. Emulator data persists under that directory until removed; restart the script after reboot.

## Test coverage

Package EditMode tests are in Tests/Editor. TSW's adapter also provides an editor integration test for real content discovery, serialized references and Undo/Redo. Source compilation and pure regression tests are separate from a live Addressables build or store verification.
