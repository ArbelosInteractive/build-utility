# DLC Pump: quick start

The short version of how to ship DLC. For the details behind each step, see [DLC-PUMP.md](DLC-PUMP.md) (releases, store exports, PlayFab) and [DLC-CONTENT.md](DLC-CONTENT.md) (Azure content).

Everything goes to **staging first**. You test there, then copy the same files to **production**. Nothing is rebuilt between staging and production.

## The windows

| Window (menu **Build Utility >**) | What it's for |
|---|---|
| **DLC Pump** | Define a release: which products, which content |
| **Release Waves** / **Icon Studio** (buttons in the DLC Pump header) | Sale dates, store icons |
| **DLC Store Export** | Create the products in Google Play, App Store Connect and PlayFab |
| **DLC Publish** | Build the Addressables, upload content, check what's live, promote to production |
| **TSW Staging Build** | Make a staging build, or switch Play Mode to staging |

## One-time setup

- Python 3.8+ and the Azure CLI installed (**Install Python packages** in DLC Store Export does the rest).
- In **DLC Publish**, click **Sign in to Azure**.
- In the **Store Export Settings** asset, set the Key Vault name (`tsw-iap`). Store credentials stay in Key Vault.

## The flow

### 1. Define the release (DLC Pump)

1. **New**, and save it in `Assets/Editor/DlcReleases`.
2. Pick the products. Fix titles and descriptions if needed (**Save listing with backup**).
3. **Discover content from project rules** fills in the content list.
4. **Validate release** and fix any errors. Then **Save release**.

New product? Add it to the IAP catalog and give it an icon (**Edit icon** on the **Products** tab).

### 2. Set the dates (Release Waves, optional)

Put the products into a wave with a start date and, optionally, an end date. PlayFab hides products until their start date, so a date in the future means the product won't show in the store, even on staging.

### 3. Create the store products (DLC Store Export)

1. Check the releases, then **Add missing products from checked releases**. Fill in prices, regions and the Apple screenshot.
2. Under **PlayFab release waves**, tick the waves for these products.
3. **Validate platform catalog export**, then **Export platform catalogs (JSON)…**.
4. In **Upload**, leave PlayFab on **Staging**, tick **PlayFab: publish the items**, and run **Dry run**, then **Upload…** (or **Upload all checked…**).

Apple and Google don't have a staging copy: they create the real products, which stay unavailable until they are approved or activated.

### 4. Build and publish the content to staging (DLC Publish)

Do this **once per platform**: switch the editor to Android, run the steps, then switch to iOS and run them again.

1. Check the releases.
2. **Build**: click the suggested button.
   - **Full build**: a new app version that hasn't shipped yet.
   - **Content update**: a version players already have. Click **Download from Azure** next to *Content-state baseline* first.
3. **Export**.
4. **Publish to staging**: leave **Also publish game data** ticked, run **Publish dry run**, then **Publish…**.

### 5. Test on staging

Use a staging build, or Play Mode switched to staging in **TSW Staging Build**. Buy, restore, and check that the content downloads.

In DLC Publish, open **Live check** and click **Check the checked releases**. The Google, Apple, PF stg, DLC stg and Data stg columns should all be green.

### 6. Go to production

1. **DLC Store Export → Upload**: switch PlayFab to **PRODUCTION** and upload **the same export**. Items that weren't uploaded to staging in exactly that form are refused.
2. **DLC Publish → Promote to production** (once per platform): leave **Also promote game data** ticked, run **Promote dry run**, then **Promote to production…**.
3. Run **Live check** again. A product is live when every production column is green and the **Live** column says YES.

Apple products also need App Review approval, and Google products need an active purchase option. Live check shows both.

## If something goes wrong

- **Undo a publish or promote**: use **Rollback** in DLC Publish. Choose staging or production.
- **Publish stopped halfway**: click **Resume publish**, or run it again. Files that already uploaded are skipped.
- **Product not in the store on staging**: run Live check. Usually the PlayFab item isn't published yet, or its start date is in the future.
- **"Full build of a published version?"**: players already have this version, so choose **Content update** instead. Only do a full build if the version hasn't shipped.

## Things to remember

- Content is stored per **platform** and **app version**. A new app version starts with no DLC: publish its content before release.
- Rebuilding the game doesn't rebuild Addressables. Rebuild them in DLC Publish only when addressable content changed. Otherwise the game build reuses the last Addressables build.
- The IAP catalog and arenas are "game data". They're uploaded with the content, so new products and arenas can appear without a new app build, as long as the game already supports them.
