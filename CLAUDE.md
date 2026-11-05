# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`com.arbelos.buildutility` is a Unity package (package.json declares Unity 2021.3 / Addressables 1.21.21, but TSW runs Unity 6000.3 with Addressables 2.9.1 and a binary catalog), not a Unity project. You can't open or build it on its own. It compiles only inside a consuming project. The main consumer is the TSW game at `../swefury-mobilegame`. That project pulls the package as a git dependency pinned to a branch (`Packages/manifest.json` → `build-utility.git#codex/dlc-pump`), so the game only sees changes after they are pushed to that branch and Unity re-resolves the package.

Each change bumps `version` in `package.json` and adds an entry at the top of `CHANGELOG.md` (Keep a Changelog style, newest first). Unity needs a `.meta` file for every new file or folder outside the `~`-suffixed folders. Create these meta files with stable GUIDs and commit them with the file.

## Layout and assemblies

- `Runtime/` (`Arbelos.BuildUtility.Runtime`): `AddressablesDownloader`, project data classes, and `Dlc/DlcContentService` (downloads `<Platform>/<appVersion>/products.json` + per-product `product.json`, maps productId to Addressables keys).
- `Editor/` (`Arbelos.BuildUtility.Editor`, Editor-only). All tools are under the **Build Utility** menu:
  - `CustomAddressableTools/`, `CustomBuildTools/`, `CustomAzureUtility/`: the original "Seamless Build" pipeline. It builds Addressables, uploads them to Azure Blob with CRC checks (Crc32.NET is in `Plugins/`), then builds the player. `BatchBuild.ChangeSettings` / `BatchBuild.BuildContent` are the `-executeMethod` entry points for CI.
  - `DlcPump/`: release authoring, build receipts, artifact inventories, store catalog export, Release Waves, Icon Studio, PlayFab review/publish, and the **DLC Publish** window (`DlcPublishWindow` → `DlcContentExport` + `DlcContentPublisher`, which runs `Tools~/DlcPublisher/publish_dlc.py`). That window is the only Azure publish path. Full user documentation is in `Documentation~/DLC-PUMP.md`.
  - `versionDefines` sets `ARBELOS_DLC_LAYOUT_V2` when Addressables is 2.0 or newer. Build-layout code branches on this define.
- `Tests/Editor/` (`Arbelos.BuildUtility.DlcPump.Tests`): NUnit EditMode tests. `overrideReferences: true` makes the tests reference `Newtonsoft.Json.dll` and `nunit.framework.dll` explicitly. Keep it that way so the tests compile in consumers.
- `Tools~/` (Unity ignores this folder): standalone tools that run outside Unity.
  - `DlcPublisher/publish_dlc.py`: Python publisher for DLC product manifests + the per-app-version catalog (`Documentation~/DLC-CONTENT.md`). Tests: `python -m unittest test_publish_dlc` in that folder.
  - `IAP UPLOADER/`: Python scripts that submit DLC Pump's platform catalog export to the stores (see below).

## DLC Pump architecture

- **The package stays game-agnostic.** All game knowledge goes through interfaces that the consuming project implements in its own Editor assembly and registers through `DlcProject.Adapter` in an `[InitializeOnLoad]` constructor. The interfaces are `IDlcProjectAdapter` (catalog path, backups, content discovery, product→asset mapping) and the optional `IDlcPackAuthoring` and `IDlcIconAuthoring`. TSW's implementation is `../swefury-mobilegame/Assets/Editor/DlcTswAdapter.cs`. Never reference game types (wrestlers, arenas and so on) from this package.
- The product catalog is the consumer's **Unity IAP catalog JSON**. Edits to it keep unknown fields, write backups and reject concurrent changes.
- **Exports are local and immutable.** Platform catalog export (`DlcStoreCatalogExport`) writes a new uniquely named `dlc-store-export-<timestamp>-<guid>/` folder each time and never overwrites. It holds PlayFab/Apple/Google request bodies, `requests.json` (ordered endpoints and dependencies) and `validation.json`. Export makes no API calls and stores no credentials.
- Apple resource IDs exist only after creation, so the export payloads contain `{{request-id.data.id}}` bindings. The uploader resolves them at submit time. Apple's own `${price1}` local ids must stay literal. Review screenshots are resized to 1179×2556 at export, and the size and MD5 in the reserve/commit JSON are computed from the resized bytes.
- PlayFab review/publish (`DlcPlayFabPlan`/`Client`/`Window`) is the only in-editor path that writes to a live service. It is read-and-review first, then an explicit publish. It uses ETag checks for each item, publishes item by item (not as one transaction), reads each item back to verify it, and writes a JSON receipt. The title secret comes from an OS environment variable (default `DLC_PUMP_PLAYFAB_SECRET`) and is never saved in an asset.

## IAP uploader (`Tools~/IAP UPLOADER/`)

Its README is detailed; read it before changing the scripts. Main points:

- `upload_all.py` runs `upload_apple_iap.py` → `upload_google_iap.py` → `upload_playfab_items.py` as subprocesses and stops at the first platform that fails. The front end is the Upload section of the Unity **DLC Store Export** window (`DlcStoreExportWindow` + `DlcStoreUploader`), which runs `upload_all.py --export <folder> --progress` and reads its `@@items` / `@@platform` / `@@item` lines; the Tk window was removed. `iap_items.py` lists an export's items (`--list`).
- `upload_config.json` holds the paths and the Key Vault name. Glob paths such as `dlc-store-export*/apple` resolve to the **most recently modified** match.
- Credentials come only from Azure Key Vault (`iap_secrets.py`, the user runs `az login`). There are no credential files.
- The scripts are written so re-running them is safe: existing products are updated instead of created again. `iap_common.py` writes a summary and a log for each run to `logs/`.
- Setup: `pip install requests "pyjwt[crypto]" google-auth azure-identity azure-keyvault-secrets`
- Check safely: `python upload_all.py --dry-run`, then a single item, for example `python upload_all.py --only apple --item 0001`. Each script also runs on its own with `--dry-run`, `-v` and `--log-file`.
- The scripts have only been tested against mocked APIs. Don't run them without `--dry-run` unless the user asks, because they write to live stores.

## Running tests

No CLI or test runner works in this repo by itself. Run the EditMode tests from the consuming project: add `"testables": ["com.arbelos.buildutility"]` to its `Packages/manifest.json`, then use Window → General → Test Runner, or batch mode:

```
Unity.exe -batchmode -projectPath ../swefury-mobilegame -runTests -testPlatform EditMode -assemblyNames Arbelos.BuildUtility.DlcPump.Tests -testResults results.xml
```

Add `-testFilter <Namespace.Class.Method>` to run a single test. TSW also has `Tools/StoryAudit/validate-dlc-liveops.py --unity <Unity.exe>` for offline publishing/inventory checks and `Tools/DlcStaging` (Azurite emulator) for staging publish.

## Gotchas

- `.gitignore` ignores `dlc-store-export*/`, `*.p8`, `*credentials*.json`, `.env` and similar files. Export folders contain store payloads and must stay out of git.
- `.gitignore` lists `/Tools~/IAP Uploader/logs/`, but the folder is named `IAP UPLOADER`. The rule only works because `core.ignorecase=true` on Windows.
