# DLC content on Azure

This pipeline publishes one small JSON manifest per product, plus the Addressables remote catalog and bundles, to Azure Blob Storage. The game uses the manifests to find each owned product's Addressables keys. Ownership comes from the game's entitlement code; this pipeline doesn't decide who owns what.

## Staging and production

There are two containers in the same storage account, with the same layout:

| | Staging (`dlc-staging`) | Production (`dlc-production`) |
|---|---|---|
| Publishing settings | `container` | `productionContainer` |
| Game URL (`Resources/DlcRemoteConfig`) | `stagingCdnBaseUrl` | `cdnBaseUrl` |
| Written by | **Publish** (and Rollback with *Staging*) | **Promote to production** only (and Rollback with *Production*) |
| Read by | staging builds and the Editor's staging Play Mode | store builds |
| Content-state baseline | stored here | not copied |
| App Addressables for Cloud Build | stored here | not copied |

The flow: build, export, **Publish** to staging, test with a staging build (or switch Play Mode to staging in **Build Utility > TSW Staging Build**), then **Promote to production** in DLC Publish. Promote copies the tested files byte for byte; nothing is rebuilt or re-exported. The PlayFab side works the same way: DLC Store Export uploads to the staging title first (see `Tools~/IAP UPLOADER/README.md`).

One build has to work from both containers, so the URL is read at runtime. The Addressables Remote.LoadPath is `{Arbelos.BuildUtility.Runtime.DlcEnvironment.ContentUrl}/[BuildTarget]/[UnityEngine.Application.version]`: the part in braces is evaluated by Addressables when the game starts, from `DlcRemoteConfig`. It's `cdnBaseUrl` unless the game called `DlcEnvironment.UseStaging(true)` before Addressables initialized (staging builds and the staging Play Mode do). Staging never falls back to production: an empty `stagingCdnBaseUrl` loads no DLC.

`promote` order, as for publish: every bundle in the staging version folder (create-only, identical skipped, a different file with the same name fails), the catalog and its `.hash`, each promoted product's `v<N>/product.json` (create-only), then production's `products.json` (If-Match). With *Only products of the checked releases* (`--product`), only those products' entries are written into production's `products.json`; the bundles and the catalog are always copied whole, and products left out stay invisible to production players. Stopping is safe at any point; run it again and the files already copied are skipped. The copy is server-side where the Azure SDK supports it and falls back to download + upload.

```
python Tools~/DlcPublisher/publish_dlc.py promote --account-url ... --container dlc-staging --to-container dlc-production --platform Android --app-version 1.4.0 --dry-run
```

The catalog name check applies to production too: promoting a full rebuild of a version production already has fails unless *allow a new catalog name in production* (`--allow-new-catalog`) is ticked. Roll staging and production back separately (Rollback has a *Container* choice); a later promote copies staging's entries over production's.

**Builds made with a fixed URL.** An app version built with a fixed Remote.LoadPath (for example `https://…/dlc-staging/[BuildTarget]/…`) loads from that URL forever, whichever container its content is in, and old builds can't read the runtime URL. Keep the fixed URL for content updates of an app version that shipped with it, and use the runtime URL from the next app version on: DLC Publish warns while the active profile has a fixed URL and has a **Use runtime content URL** button. The export only warns.

## Storage layout

```
dlc-staging/ (and dlc-production/)             container (DlcPublishingSettings.container / productionContainer)
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
  _build-state/
    Android/1.4.0/addressables_content_state.bin   the full build's content-state file, for content updates;
                                                   stored once, never read by the game
    Android/1.4.0/player-addressables.zip          the app's own Addressables output, restored by Cloud Build
```

`products.json`:

```json
{ "schemaVersion": 1, "platform": "Android", "appVersion": "1.4.0", "catalogBuild": "<catalog hash>", "updatedUtc": "...",
  "products": [ { "productId": "wrestler_jazz", "current": "v2", "versions": ["v2", "v1"] } ] }
```

`<productId>/v<N>/product.json`:

```json
{ "schemaVersion": 1, "productId": "wrestler_jazz", "version": 2, "platform": "Android", "appVersion": "1.4.0",
  "releaseId": "...", "minimumClientVersion": "", "keys": ["Jazz/Wrestling Gear/skin-material/...", "..."],
  "catalogBuild": "<catalog hash>", "notes": "", "publishedUtc": "..." }
```

`keys` are the `addressablesKey` values of the release's content entries: real Addressables addresses, never renamed or labelled. A content entry with a **blank** `addressablesKey` is local content that ships in the app (for example wrestler definitions or animation clips referenced from Resources); it is ignored. A product whose entries are all blank (or that has no entries) is still published, with `"keys": []`, so `products.json` records every product that belongs to the app version even when there is nothing to download. The game reports such a product as `Ready` with no keys, and `DlcContentService.PublishedProductIds` / `IsPublished(id)` list everything published for the running app version, owned or not. A pack lists the same addresses as its member products.

## The app-version folder rule

Each app version has its own catalog, so a build points at its own folder only. In every profile used for a shipping build, set:

| Profile variable | Value |
|---|---|
| Remote.LoadPath | `{Arbelos.BuildUtility.Runtime.DlcEnvironment.ContentUrl}/[BuildTarget]/[UnityEngine.Application.version]` |
| Remote.BuildPath | `ServerData/[BuildTarget]/[UnityEngine.Application.version]` |

Keep **Build Remote Catalog** on and group **Bundle Naming** set to *Append Hash*, so unchanged bundles keep their file names. The exporter refuses to run unless the selected profile's paths end in `/<Platform>/<appVersion>`, and checks that `Resources/DlcRemoteConfig` has both URLs (different ones, ending in the two container names). A fixed Remote.LoadPath is only a warning (see *Staging and production*).

A content update for an app version that has already shipped uses Addressables' *Update a Previous Build* with that version's content-state file. The catalog name stays the same, and new or changed bundles get new hash names. A new app version uses a full build and starts with an empty folder and an empty `products.json`.

### The content-state baseline

Only full Addressables builds write `addressables_content_state.bin` (by default `Assets/AddressableAssetsData/<Platform>/`, which is git-ignored), and the next full build overwrites it. Every content update of an app version must be built against the file from the full build players have, so the publisher keeps a copy in the container:

- **Stored once.** When you export, the exporter includes the local file only if it belongs to the catalog being exported (its `playerVersion` matches `catalog_<playerVersion>`). Publishing then uploads it to `_build-state/<Platform>/<appVersion>/` in step 2, after the catalog.
- **Never overwritten by a content update.** Updates don't write the file, so an update's export either includes the same file, which is skipped, or none, in which case the exporter warns and the stored one stays.
- **A different file fails the run** before anything is uploaded: it means a full rebuild of a version that already has a baseline. Ticking **allow a new catalog name** replaces it, for a version that hasn't shipped.
- **Download it** with **Download from Azure** next to *Content-state baseline* in the Build section of DLC Publish. It saves to `Library/DlcBaselines/<Platform>/<appVersion>/` and fills in the field. On the command line: `publish_dlc.py baseline --account-url ... --platform Android --app-version 1.4.0 --out <file>`.

The file lists asset paths, GUIDs and bundle hashes; there are no credentials in it. With public read access anyone who knows the URL can download it, which reveals little beyond the public catalog and bundles.

If *Build Addressables on Player Build* is on, building the app runs another full Addressables build with a new catalog name. Publish that build's content, or turn the option off for release builds, so the stored baseline matches the app players install.

Content update, step by step (once per platform): switch the editor to Android or iOS, check the releases, click **Download from Azure**, click **Content update**, then export and publish as usual.

Clients that have already shipped keep the load path baked into their catalog, so changing the profile only affects new builds.

### App Addressables for Cloud Build

Every player build contains the app's own Addressables output: `Library/com.unity.addressables/aa/<Platform>` (settings.json, the local catalog, link.xml and the content built into the app). Unity copies that folder into the player; it doesn't have to be built in the same place. So Addressables are built only on your machine, with DLC Publish, and the cloud build ships that output:

- **Export** packs it as `player-addressables.zip` when it belongs to the exported catalog, with the CRCs of the remote build's files (for `GameAddressableData`). If it doesn't (Addressables rebuilt since), the export warns and leaves it out.
- **Publish** stores it at `_build-state/<Platform>/<appVersion>/player-addressables.zip` in the staging container during step 2. An identical file is skipped; a different one (after a content update, say) replaces it.
- **Unity Cloud Build** calls `DlcPlayerContent.RestoreForCloudBuild()` in its pre-export method, instead of building Addressables. It downloads the package for the active platform and `PlayerSettings.bundleVersion`, checks it's for that platform and version, unpacks it into `Library/com.unity.addressables/aa/<Platform>` and writes the CRCs into the `GameAddressableData` asset in Resources. Leave the build configuration's own Addressables build off, and *Build Addressables on Player Build* off.

The app then ships exactly the build whose catalog was published, so its built-in content always matches the remote catalog.

Cloud Build reads the storage account and staging container from the `DlcPublishingSettings` asset (or `DLC_STORAGE_ACCOUNT` / `DLC_STORAGE_CONTAINER`). To sign in, set `DLC_AZURE_TENANT_ID`, `DLC_AZURE_CLIENT_ID` and `DLC_AZURE_CLIENT_SECRET` in the build target's environment variables (`TENANT_ID`, `CLIENT_ID`, `CLIENT_SECRET` also work): a service principal with **Storage Blob Data Reader** on the staging container. With none set it reads anonymously, which works only if the container allows public read. A missing package fails the build: publish a build of that platform and app version first.

## One-time Azure setup

1. **Storage account** (StorageV2). Under *Data protection*, turn on **Blob versioning** and **soft delete for blobs and containers** (for example, 30 days). Mistaken overwrites or deletes can then be recovered in the portal. The tools never configure this themselves.
2. **Containers** `dlc-staging` and `dlc-production`. With the default *PublicRead* access mode, set the public access level of both to **Blob** (anonymous read of blobs, no listing), and enable *Allow Blob anonymous access* on the account.
3. **RBAC.** Give each person who publishes **Storage Blob Data Contributor** on the account or the container. Publishing signs in with `az login` (AzureCliCredential). No account keys or SAS tokens are stored anywhere.
4. **CDN** (Azure Front Door or CDN). Route `/dlc-staging/*` and `/dlc-production/*` to the storage account (one origin without an origin path serves both). Put the public URLs into `Resources/DlcRemoteConfig`: `stagingCdnBaseUrl` (…/dlc-staging) and `cdnBaseUrl` (…/dlc-production). Have the CDN honour origin cache headers. The publisher sets `Cache-Control: public, max-age=60` on `products.json` and the catalog, and `public, max-age=31536000, immutable` on bundles and `product.json`.
5. **Python** 3.8 or newer: `pip install azure-identity azure-storage-blob`, plus the Azure CLI (`winget install Microsoft.AzureCLI`).
6. **Sign in** with **Sign in to Azure** in the DLC Publish window, which runs `az login` in your browser; for a guest account, set `azureTenant` in the publishing settings first. The status line then shows who is signed in and whether that account can read the container. **Check again** re-tests it and **Sign out** runs `az logout`. On a build server, run `az login --service-principal ...` before publishing instead.

*SignedUrl* access mode is reserved for paid bundles. Implementing it means adding an `IDlcUrlSigner` and having Addressables sign its requests too; see the comment in `Runtime/Dlc/DlcRemoteConfig.cs`.

## Export, then publish

1. Open **Build Utility > DLC Publish** (also reachable from the DLC Pump header). Pick the publishing settings and the folder that holds your `DlcRelease` assets; every release in it (and its subfolders) is listed. Check the releases to ship.
2. Switch the editor to Android or iOS and select that platform's Addressables profile (the build and export use the active one). In **Build**, the window reads what's already published for this platform and app version and suggests one of the two buttons: **Full build** when nothing is published yet, **Content update** when a catalog is already there (players of that version only load that catalog). Both buttons stay available; building against the suggestion asks for confirmation first. A content update needs the baseline: **Download from Azure** fetches the stored one.
3. Click **Validate (dry run)**, then **Export**. The export fails, and writes nothing, if:
   - a content entry's productId isn't in the release's productIds
   - a non-blank `addressablesKey` isn't an Addressables address
   - a key isn't in the built remote catalog
   - a product is in two selected releases
   - the profile paths break the app-version rule

   The export goes to `<stagingDirectory>/<Platform>/<appVersion>/<utc>/` and contains `export.json`, `validation.json` and one draft `product.json` per product. A product's version number is assigned when it's published.
4. In the same window, choose the export, then click **Publish dry run** and then **Publish…**. This publishes to the **staging** container; test it, then use **Promote to production** (see *Staging and production*). *Only products of the checked releases* passes `--product` for just those products. **Status** prints the live `products.json`, and the **Rollback** section runs the rollback commands below. On the command line:

```
python Tools~/DlcPublisher/publish_dlc.py publish --staging <export dir> --account-url https://<account>.blob.core.windows.net --dry-run
python Tools~/DlcPublisher/publish_dlc.py publish --staging <export dir> --account-url ... --product wrestler_jazz
```

Before uploading anything, the publisher checks the catalog name. If the app-version folder already has a catalog with a different name, the run fails. That happens after a full rebuild of a version that already has a published catalog: installed players of that version keep loading the old catalog, so new content would never reach them. Use a content update instead, which keeps the name. For a version that hasn't shipped yet, tick **allow a new catalog name** in the DLC Publish window (or pass `--allow-new-catalog`); it resets after each publish, and the old catalog is left in place.

The order is always the same:

1. **Bundles.** An existing bundle with the same MD5 is skipped. The same name with different content is an error. A bundle that fails doesn't stop the other bundles, but the run stops after this step, before the catalog.
2. **The catalog, then its `.hash`.**
3. **Each product's next `v<N>/product.json`.** These are created only if the version doesn't already exist; an existing `v<N>` fails the run.
4. **`products.json` last,** written with `If-Match` on the ETag read before the run. If someone else published in the meantime, the run fails without overwriting their changes. Run it again.

A failed upload is retried twice (after 3 s and 10 s) before it counts as an error. Apart from failed bundles, the first error stops the run. Nothing is ever deleted.

If a product's published keys are missing from the new version, the publisher prints a warning, because keys should only ever be added. Each run prints a summary of what was created and skipped and writes `Logs/dlc-publish/publish_<date>_<time>.log`.

### Progress, cancelling and resuming

While a publish runs, the DLC Publish window shows a progress bar: the step (1 to 4), megabytes uploaded and the file being uploaded during bundles and the catalog, and a count during manifests. The output streams into the report as it arrives. **Cancel** stops the script.

Players see nothing from a publish until `products.json` is written at the very end, so stopping early is safe. After a publish fails or is cancelled, the window shows **Resume publish**, which runs the same publish again with the same export, products and catalog option. The same happens if you run the command again yourself:

- Bundles and catalog files already uploaded are skipped (same MD5).
- A `v<N>/product.json` written by the interrupted run but not yet listed in `products.json` is reused when it holds the same draft, so the product doesn't skip a version number. If you exported again with different keys, the next number is used instead.

The window stops the script only if it prints nothing, not even progress, for 10 minutes. There's no overall time limit. `--progress` makes the script print the `@@progress {json}` lines the window reads; they aren't written to the log.

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
var service = new DlcContentService(remoteConfig);          // DlcRemoteConfig: cdnBaseUrl, or stagingCdnBaseUrl after DlcEnvironment.UseStaging(true)
await service.LoadAllAsync();                                // every launch and on resume, after Addressables is initialized
if (service.TryGetKeys("wrestler_jazz", out var keys)) { /* the game's own ownership check decides whether to use them */ }
```

`LoadAllAsync()` loads the manifest of every product in `products.json`, owned or not; ownership stays in the game's entitlement code (PlayFab for TSW). Call it on every launch: `products.json` is always checked with the server, so DLC published for the same app version since the last run appears (an unchanged list costs a `304`). `LastLoadWasOnline` tells you whether that check reached the server, so you can retry later. `LoadAsync(ids)` does the same for a given list of IDs only.

For `<Platform>/<Application.version>/`, the service:

- **Fetches `products.json`** with `If-None-Match`.
- **Downloads only what's missing.** It fetches each owned product's current `product.json` only if that version isn't already cached; versions never change, so a cached copy is always valid.
- **Caches everything** under `persistentDataPath/dlc-content/<Platform>/<appVersion>/`. When offline it uses the cached `products.json`, falling back to the newest cached version that file lists.
- **Skips products** whose `minimumClientVersion` is newer than the app. The export always writes it empty: content is already published per app version, so it's kept only for apps already installed.
- **Checks every key** with `Addressables.LoadResourceLocationsAsync`. If keys are missing, it runs one `CheckForCatalogUpdates` + `UpdateCatalogs` and checks again.

Each product ends with one of these statuses: `Ready`, `NotReady` (keys still missing), `NotInAppVersion`, `RequiresNewerClient` or `Unavailable`. The service never calls `LoadContentCatalogAsync`. Run it after `AddressablesDownloader` has initialized, so the two don't update catalogs at the same time.

## Tests

- **Unity:** EditMode tests are in `Tests/Editor/DlcContentExportTests.cs`.
- **Publisher:** `cd Tools~/DlcPublisher && python -m unittest test_publish_dlc` runs against an in-memory store. The real Azure calls have not been tested against a live account. Run `--dry-run` and publish a single product to a test container first.
