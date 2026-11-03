# DLC content on Azure

This pipeline publishes one small JSON manifest per product, plus the Addressables remote catalog and bundles, to Azure Blob Storage. The game uses the manifests to find each owned product's Addressables keys. Ownership comes from the game's entitlement code; this pipeline doesn't decide who owns what.

## Storage layout

```
dlc-content/                                   container (DlcPublishingSettings.container)
  Android/                                     [BuildTarget] name
    1.4.0/                                     Application.version, one folder per app version
      catalog_<build>.bin + catalog_<build>.hash   the one remote catalog for this app version
      <bundles...>                             content-hash names; never overwritten
      products.json                            mutable; the only file the game reads first
      wrestler_jazz/
        v1/product.json                        immutable
        v2/product.json
    1.5.0/                                     starts empty; a product absent here doesn't exist for 1.5.0 clients
  iOS/ ...
```

`products.json`:

```json
{ "schemaVersion": 1, "platform": "Android", "appVersion": "1.4.0", "catalogBuild": "<catalog hash>", "updatedUtc": "...",
  "products": [ { "productId": "wrestler_jazz", "current": "v2", "versions": ["v2", "v1"] } ] }
```

`<productId>/v<N>/product.json`:

```json
{ "schemaVersion": 1, "productId": "wrestler_jazz", "version": 2, "platform": "Android", "appVersion": "1.4.0",
  "releaseId": "...", "target": "Android", "minimumClientVersion": "", "keys": ["Jazz/Wrestling Gear/skin-material/...", "..."],
  "catalogBuild": "<catalog hash>", "notes": "", "publishedUtc": "..." }
```

`keys` are the `addressablesKey` values of the release's content entries: real Addressables addresses, never renamed or labelled. A content entry with a **blank** `addressablesKey` is local content that ships in the app (for example wrestler definitions or animation clips referenced from Resources); it is ignored. A product whose entries are all blank (or that has no entries) is still published, with `"keys": []`, so `products.json` records every product that belongs to the app version even when there is nothing to download. The game reports such a product as `Ready` with no keys, and `DlcContentService.PublishedProductIds` / `IsPublished(id)` list everything published for the running app version, owned or not. A pack lists the same addresses as its member products.

## The app-version folder rule

Each app version has its own catalog, so a build points at its own folder only. In every profile used for a shipping build, set:

| Profile variable | Value |
|---|---|
| Remote.LoadPath | `<cdnBaseUrl>/[BuildTarget]/[UnityEngine.Application.version]` |
| Remote.BuildPath | `ServerData/[BuildTarget]/[UnityEngine.Application.version]` |

Keep **Build Remote Catalog** on and group **Bundle Naming** set to *Append Hash*, so unchanged bundles keep their file names. The exporter refuses to run unless the selected profile's paths end in `/<Platform>/<appVersion>` and Remote.LoadPath equals `cdnBaseUrl` + that suffix.

A content update for an app version that has already shipped uses Addressables' *Update a Previous Build* with that version's content-state file. The catalog name stays the same, and new or changed bundles get new hash names. A new app version uses a full build and starts with an empty folder and an empty `products.json`.

Clients that have already shipped keep the load path baked into their catalog, so changing the profile only affects new builds.

## One-time Azure setup

1. **Storage account** (StorageV2). Under *Data protection*, turn on **Blob versioning** and **soft delete for blobs and containers** (for example, 30 days). Mistaken overwrites or deletes can then be recovered in the portal. The tools never configure this themselves.
2. **Container** `dlc-content`. With the default *PublicRead* access mode, set its public access level to **Blob** (anonymous read of blobs, no listing), and enable *Allow Blob anonymous access* on the account.
3. **RBAC.** Give each person who publishes **Storage Blob Data Contributor** on the account or the container. Publishing signs in with `az login` (AzureCliCredential). No account keys or SAS tokens are stored anywhere.
4. **CDN** (Azure Front Door or CDN). Create an origin for the storage account with the origin path `/dlc-content`, or route `/dlc-content/*` to it. Put that public URL into `DlcPublishingSettings.cdnBaseUrl` and `DlcRemoteConfig.cdnBaseUrl`. Have the CDN honour origin cache headers. The publisher sets `Cache-Control: public, max-age=60` on `products.json` and the catalog, and `public, max-age=31536000, immutable` on bundles and `product.json`.
5. **Python** 3.8 or newer: `pip install azure-identity azure-storage-blob`, plus the Azure CLI (`winget install Microsoft.AzureCLI`).
6. **Sign in** with **Sign in to Azure** in the DLC Publish window, which runs `az login` in your browser; for a guest account, set `azureTenant` in the publishing settings first. The status line then shows who is signed in and whether that account can read the container. **Check again** re-tests it and **Sign out** runs `az logout`. On a build server, run `az login --service-principal ...` before publishing instead.

*SignedUrl* access mode is reserved for paid bundles. Implementing it means adding an `IDlcUrlSigner` and having Addressables sign its requests too; see the comment in `Runtime/Dlc/DlcRemoteConfig.cs`.

## Export, then publish

1. Switch to the target platform, then build Addressables (full or content update) with the release's profile.
2. Open **Build Utility > DLC Publish** (also reachable from the DLC Pump header). Pick the publishing settings and the folder that holds your `DlcRelease` assets; every release in it (and its subfolders) is listed. Check the releases to ship, then click **Validate (dry run)**, then **Export**. The export fails, and writes nothing, if:
   - a content entry's productId isn't in the release's productIds
   - a non-blank `addressablesKey` isn't an Addressables address
   - a key isn't in the built remote catalog
   - a product is in two selected releases
   - the profile paths break the app-version rule

   The export goes to `<stagingDirectory>/<Platform>/<appVersion>/<utc>/` and contains `export.json`, `validation.json` and one draft `product.json` per product. A product's version number is assigned when it's published.
3. In the same window, choose the export, then click **Publish dry run** and then **Publish…**. *Only products of the checked releases* passes `--product` for just those products. **Status** prints the live `products.json`, and the **Rollback** section runs the rollback commands below. On the command line:

```
python Tools~/DlcPublisher/publish_dlc.py publish --staging <export dir> --account-url https://<account>.blob.core.windows.net --dry-run
python Tools~/DlcPublisher/publish_dlc.py publish --staging <export dir> --account-url ... --product wrestler_jazz
```

Before uploading anything, the publisher checks the catalog name. If the app-version folder already has a catalog with a different name, the run fails. That happens after a full rebuild of a version that already has a published catalog: installed players of that version keep loading the old catalog, so new content would never reach them. Use a content update instead, which keeps the name. For a version that hasn't shipped yet, tick **allow a new catalog name** in the DLC Publish window (or pass `--allow-new-catalog`); it resets after each publish, and the old catalog is left in place.

The order is always the same, and the first error stops the run:

1. **Bundles.** An existing bundle with the same MD5 is skipped. The same name with different content fails the run.
2. **The catalog, then its `.hash`.**
3. **Each product's next `v<N>/product.json`.** These are created only if the version doesn't already exist; an existing `v<N>` fails the run.
4. **`products.json` last,** written with `If-Match` on the ETag read before the run. If someone else published in the meantime, the run fails without overwriting their changes. Run it again.

If a product's published keys are missing from the new version, the publisher prints a warning, because keys should only ever be added. Each run prints a summary of what was created and skipped and writes `Logs/dlc-publish/publish_<date>_<time>.log`.

## Rollback

Rolling back only rewrites `products.json`. Version folders are never deleted.

```
python Tools~/DlcPublisher/publish_dlc.py rollback --account-url ... --platform Android --app-version 1.4.0 --product wrestler_jazz --to v1
python Tools~/DlcPublisher/publish_dlc.py rollback --account-url ... --platform Android --app-version 1.4.0 --product wrestler_jazz --drop v2
python Tools~/DlcPublisher/publish_dlc.py status   --account-url ... --platform Android --app-version 1.4.0
```

`--drop` removes a version from the list; if it was current, the next newest version becomes current. The last remaining version can't be dropped. Don't delete `v<N>` folders, bundles or catalogs in the portal: a client of that app version may still have them cached or referenced. Soft delete and versioning are there to recover from mistakes. Bundles that are no longer used cost storage, nothing else.

After a rollback, keys from the newer version that also need older bundles still resolve, because bundles are never removed. The catalog is not rolled back; only the product's key list changes.

## In the game

```csharp
var service = new DlcContentService(remoteConfig);          // DlcRemoteConfig asset with cdnBaseUrl
await service.LoadAsync(ownedProductIds);                    // from your entitlement code, after Addressables is initialized
if (service.TryGetKeys("wrestler_jazz", out var keys)) { /* load or download by address */ }
```

For `<Platform>/<Application.version>/`, the service:

- **Fetches `products.json`** with `If-None-Match`.
- **Downloads only what's missing.** It fetches each owned product's current `product.json` only if that version isn't already cached; versions never change, so a cached copy is always valid.
- **Caches everything** under `persistentDataPath/dlc-content/<Platform>/<appVersion>/`. When offline it uses the cached `products.json`, falling back to the newest cached version that file lists.
- **Skips products** whose `minimumClientVersion` is newer than the app.
- **Checks every key** with `Addressables.LoadResourceLocationsAsync`. If keys are missing, it runs one `CheckForCatalogUpdates` + `UpdateCatalogs` and checks again.

Each product ends with one of these statuses: `Ready`, `NotReady` (keys still missing), `NotInAppVersion`, `RequiresNewerClient` or `Unavailable`. The service never calls `LoadContentCatalogAsync`. Run it after `AddressablesDownloader` has initialized, so the two don't update catalogs at the same time.

## Tests

- **Unity:** EditMode tests are in `Tests/Editor/DlcContentExportTests.cs`.
- **Publisher:** `cd Tools~/DlcPublisher && python -m unittest test_publish_dlc` runs against an in-memory store. The real Azure calls have not been tested against a live account. Run `--dry-run` and publish a single product to a test container first.
