# IAP upload tools

Scripts that upload in-app purchase definitions from JSON files to three services:

| Script | Uploads to | Input |
|---|---|---|
| `upload_apple_iap.py` | App Store Connect (in-app purchases) | one numbered folder per product (`0001`, `0002`, ...) |
| `upload_google_iap.py` | Google Play (one-time products) | a `batchUpdate` JSON file |
| `upload_playfab_items.py` | PlayFab Economy v2 (catalog items) | one `CreateDraftItem` JSON per item |
| `upload_all.py` | runs the three above, in that order | `upload_config.json` |
| `upload_gui.py` | a window that lists the products and has upload buttons (runs `upload_all.py`) | `upload_config.json` |

`iap_common.py` (logging and the summary) and `iap_secrets.py` (reads the credentials from Azure Key Vault) are helpers the scripts import. Keep them in the same folder as the scripts.

The store credentials (Apple key, Google service account, PlayFab secret) are **not stored in files**. They live in an Azure Key Vault, and each person signs in with their own Azure account (section 3).

> These scripts were tested against mocked versions of the three APIs and of Key Vault, not the live services. Always do a `--dry-run`, then upload a single product, before running everything.

---

## 1. Install

Requires Python 3.8 or newer and the Azure CLI. In PowerShell or Command Prompt:

```
pip install requests "pyjwt[crypto]" google-auth azure-identity azure-keyvault-secrets
winget install Microsoft.AzureCLI
```

Close and reopen the terminal after installing the Azure CLI so `az` is found.

Installing everything is simplest. If `pip` isn't recognised, try `py -m pip install ...` or `python -m pip install ...`. `Open IAP Uploader.bat` installs the pip packages for you on its first run, and, if `az` is missing, the Azure CLI too (through winget, with a Windows permission prompt).

---

## 2. Folder layout

Keep the scripts together, with your data folders beside them:

```
upload_all.py
upload_apple_iap.py
upload_google_iap.py
upload_playfab_items.py
iap_common.py
iap_secrets.py
upload_gui.py
upload_config.json
apple\
    0001\  00-pricepoint.json, 01-create.json, 02-localization-01.json, 03-pricing.json,
           04-availability.json, 05-screenshot-reserve.json, 06-screenshot-commit.json,
           review-screenshot.png
    0002\  ...
google\
    0001-batch.json
playfab\
    0001-create.json, 0002-create.json, ...
logs\            (created automatically)
```

The data folder locations are set in `upload_config.json` (section 4), so they can live anywhere.

---

## 3. Credentials: Azure Key Vault

All credentials are read from one Azure Key Vault. Nothing secret is kept in the project folder, so the project (but not your exported product JSON, if you leave it out of git) can be shared safely. Everyone signs in with their own Azure account.

### 3a. The secrets the scripts read

Secret names may only contain letters, digits and hyphens. These exact names are used:

| Secret name | Value |
|---|---|
| `apple-key-id` | the App Store Connect API Key ID |
| `apple-issuer-id` | the App Store Connect Issuer ID |
| `apple-p8` | the full contents of the `AuthKey_XXXX.p8` file |
| `google-service-account` | the full contents of the Google service account JSON key |
| `playfab-title-id` | the PlayFab title ID |
| `playfab-secret-key` | the PlayFab secret key |

Where the values come from:
- **Apple:** App Store Connect > Users and Access > Integrations > App Store Connect API. Generate a key (App Manager or Admin access), download the `.p8` (Apple only lets you download it once) and copy the Key ID and Issuer ID from that page.
- **Google:** in Google Cloud, enable the **Google Play Android Developer API** in your project, create a service account (IAM & Admin > Service Accounts), then Keys > Add key > Create new key > JSON. In Play Console > Users and permissions > Invite new users, add the service account's email with access to the app, including in-app products.
- **PlayFab:** in Game Manager, the title ID is on the title's overview and the secret key is under Settings > Secret keys.

### 3b. One-time vault set-up (the vault owner)

1. **Create the vault.** Azure portal > Key vaults > Create. Use the Standard tier, a globally unique name (for example `arbelos-iap-kv`) and, on the Access configuration tab, **Azure role-based access control**. Keep soft delete on and enable purge protection. Or from the CLI:
   ```
   az login
   az keyvault create --name arbelos-iap-kv --resource-group <your-group> --location <region> --enable-rbac-authorization true
   ```
2. **Give yourself write access.** On the vault: Access control (IAM) > Add role assignment > **Key Vault Secrets Officer** > your account. (Creating the vault does not give you access to its secrets.)
3. **Add the six secrets.**
   ```
   az keyvault secret set --vault-name arbelos-iap-kv --name apple-key-id --value "ABC123DEFG"
   az keyvault secret set --vault-name arbelos-iap-kv --name apple-issuer-id --value "69a6de70-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
   az keyvault secret set --vault-name arbelos-iap-kv --name apple-p8 --file AuthKey_ABC123DEFG.p8
   az keyvault secret set --vault-name arbelos-iap-kv --name google-service-account --file google_key.json
   az keyvault secret set --vault-name arbelos-iap-kv --name playfab-title-id --value "ABCD1"
   az keyvault secret set --vault-name arbelos-iap-kv --name playfab-secret-key --value "YOUR_SECRET_KEY"
   ```
   Or add them in the portal (Secrets > Generate/Import). For the two file-based ones, paste the whole file contents. Afterwards delete the local `.p8` and key JSON, keeping a copy in your password manager. To change a value later, set the secret again; Key Vault keeps the old versions.
4. **Give other people read-only access.** On the vault: Access control (IAM) > Add role assignment > **Key Vault Secrets User** > the person's account. People outside your organisation need to be invited first (Microsoft Entra ID > Users > Invite external user). To remove someone, delete their role assignment, and rotate the keys if they could already read them.

### 3c. Everyone who runs the uploads

1. Install the packages and the Azure CLI (section 1).
2. Set `keyvault_name` in `upload_config.json` (section 4), or in the window under **Settings...**.
3. Sign in: run `az login` in a terminal, or click **Sign in to Azure** in the window. A browser opens. If you are a guest in someone else's Azure tenant, sign in with `az login --tenant <tenant-id-or-domain>`, or put the tenant in `azure_tenant` in the config so the window's button does it for you.

The tools use your `az login` session. There is no key to copy and nothing to paste into a file. Check access any time with:
```
az keyvault secret show --vault-name arbelos-iap-kv --name apple-key-id --query name
```

A dry run (`--dry-run`) sends nothing and needs no sign-in. Real runs check up front that all the needed secrets can be read, and stop before uploading anything if one can't.

---

## 4. Config file for the wrapper: `upload_config.json`

```json
{
  "keyvault_name": "arbelos-iap-kv",
  "azure_tenant": "",

  "apple_root": "dlc-store-export*/apple",
  "google_input": "dlc-store-export*/google",
  "playfab_input": "dlc-store-export*/playfab",
  "log_dir": "logs"
}
```

| Key | Meaning |
|---|---|
| `keyvault_name` | the name of your Azure Key Vault (just the name, not the URL) |
| `azure_tenant` | optional; only the window's **Sign in to Azure** button uses it, for guest accounts |
| `apple_root` | the folder that contains `0001`, `0002`, ... |
| `google_input` | the Google batch JSON file, or a folder of them |
| `playfab_input` | the folder with the PlayFab JSON files |
| `log_dir` | where log files are written |

Relative paths are relative to the config file's folder. Absolute paths work too; on Windows use forward slashes (`C:/data/apple`) or doubled backslashes. The product JSON folders can live anywhere, including outside the project, so they don't have to be in git. If the export folder is not inside the config's folder, use `..`, for example `../../dlc-store-export*/apple`.

**Wildcards:** `*` and `?` work in these paths, so `dlc-store-export*/apple` finds `dlc-store-export-2026-10-27/apple`. If several folders match, the most recently modified one is used. The wrapper prints the folders it picked at the start of each run, so check that line. If nothing matches, it reports that the path does not exist.

The older keys `apple_credentials`, `google_credentials` and `playfab_credentials` are no longer used and can be deleted.

---

## 5. Run everything: `upload_all.py`

```
python upload_all.py --dry-run
python upload_all.py
```

| Option | What it does |
|---|---|
| `--dry-run` | checks the files and prints what would happen; sends nothing, needs no credentials or sign-in |
| `--apple-item`, `--google-item`, `--playfab-item ID ...` | like `--item`, but per platform, so you can combine them: `python upload_all.py --only apple google --apple-item 0001 --google-item wrestler_x` |
| `--item ID ...` | upload only these items; use with exactly one platform in `--only`. Apple: folder names (`0001`), Google: product IDs, PlayFab: FriendlyIds. Example: `python upload_all.py --only apple --item 0001 0002` |
| `--only apple google playfab` | run only the platforms you list (they still run in Apple, Google, PlayFab order) |
| `--activate` | Google: make the purchase options active, i.e. purchasable (otherwise they stay as drafts) |
| `--publish` | PlayFab: publish the items (otherwise they stay as drafts) |
| `--config path` | use a different config file |

Behaviour:
- It checks every path in the config before starting, and (for real runs) that your Azure sign-in can read every secret the chosen platforms need, so a typo or a missing permission is reported up front.
- It runs Apple, then Google, then PlayFab. **The first platform that fails stops the whole run**, and the later ones are not started. The final message says what finished and what didn't run.
- The whole run is written to one log file, `logs\upload_all_<date>_<time>.log`.

Suggested first run: `python upload_all.py --dry-run`, then `python upload_all.py --only playfab` (the lowest-risk one), then the rest.

---

## 5b. The window: `upload_gui.py`

```
python upload_gui.py
```

To open it without a console, double-click **`Open IAP Uploader.bat`** (keep it in the same folder as the scripts; right-click it > Send to > Desktop (create shortcut) for a desktop icon). On the first run it installs any missing pip packages for you (and the Azure CLI through winget if it is missing). If the window can't start, the error is shown in a message box and saved to `gui_error.log`.

The window has:
- an **Azure** line under the title showing who is signed in and whether the vault can be read (green when everything works), with **Sign in to Azure** (opens the browser sign-in), **Check again** and **Sign out** buttons. Sign in before a real upload; a dry run doesn't need it.
- a list of every product found in your folders, grouped by store (Apple, Google Play, PlayFab), with its ID, name and price or type. A folder that can't be read, or an Apple product whose screenshot file is missing, is shown in red.
- buttons **Upload to Apple App Store**, **Upload to Google Play**, **Upload to PlayFab** and **Upload ALL**
- tick boxes: **Dry run** (check only, send nothing), **Google: make purchase options active**, **PlayFab: publish items**
- a live log of what the upload is doing, and a **Status** column that changes to Created / Updated / FAILED / Not processed as each item finishes
- **tick boxes** in the Upload column of each product (click the box, or select rows and press Space). Ticking a store heading ticks everything in that store, and **Select all products** above the list ticks or unticks every product. **Upload selected (N)** uploads just the ticked products, across any mix of stores (Apple, then Google, then PlayFab, stopping at the first failure). Products that can't be uploaded (shown in red) can't be ticked, and **Reload products** clears the ticks.
- **right-click** a product to upload only that product (or select several in the same store with Ctrl or Shift, then right-click). Right-clicking a store heading offers to upload everything in that store. The Dry run, Google activate and PlayFab publish options still apply, and the other rows are left alone.
- **Reload products** (re-reads the folders after you edit a JSON file), **Stop**, and **Clear log**, which empties the output window (it never touches the log files in the `logs` folder)
- **Settings...**, which edits `upload_config.json` for you: type a path or use the Browse buttons for the Apple folder, Google file or folder, PlayFab folder, the Key Vault name and optional tenant, and the log folder. Paths inside the config's folder are saved as relative paths. If a path doesn't exist yet, you're asked before saving. After saving, the product list reloads. Other keys already in the file are kept.

Real uploads ask you to confirm first, showing how many items go to which store. The window runs `upload_all.py` in the background, so everything in sections 5 to 8 still applies: existing items are updated, the first failure stops the run (the other stores show "Not run"), and the same log file is written to the `logs` folder (the path appears at the bottom of the window when the run ends).

It uses Tkinter, which is included with the standard Windows Python installer from python.org (leave "tcl/tk and IDLE" ticked). If `python upload_gui.py` says `No module named 'tkinter'`, re-run the Python installer and choose Modify > tcl/tk and IDLE.

**Stop** ends the run immediately. The product being uploaded at that moment can be left half done; upload again to finish it. Buttons are disabled while a run is in progress.

---

## 6. Running each script on its own

Each script also works alone (sign in with `az login` first) and writes its own log (`logs\apple_<date>_<time>.log` and so on). All accept `--dry-run` and `--log-file path`.

### Apple: `upload_apple_iap.py`
```
python upload_apple_iap.py --root .\apple --dry-run
python upload_apple_iap.py --root .\apple --products 0001
python upload_apple_iap.py --root .\apple
```
Options: `--root` (the folder with `0001`, `0002`, ...), `--products 0001 0002` (only those folders), `--vault` (Key Vault name; default `keyvault_name` from `upload_config.json`), `-v` (print payloads in a dry run).

Each product folder holds your six JSON files plus:
- `00-pricepoint.json`, e.g. `{"territory": "USA", "customerPrice": "9.99"}`. The price must be one of Apple's price tiers. The script looks up Apple's ID for it, and that fills `{{apple-0001-pricepoints.selected.id}}`.
- the screenshot image named in `05-screenshot-reserve.json` (`fileName`). The script uses the real file size and checksum, so the values in your JSON are ignored.

Placeholders like `{{apple-0001-create.data.id}}` are filled from the earlier steps' responses in the same run (folder + file name without its number: `01-create.json` in `0001` becomes `apple-0001-create`). Apple's own `${price1}` is left alone.

### Google: `upload_google_iap.py`
```
python upload_google_iap.py --input .\google --dry-run
python upload_google_iap.py --input .\google --only wrestler_jazz
python upload_google_iap.py --input .\google
python upload_google_iap.py --input .\google --activate
```
Options: `--input` (batch file or folder), `--vault`, `--only product_id ...`, `--activate`, `--batch-size N` (products per call, default 100; use `1` to find which product a failure comes from).

The service account JSON is read from the `google-service-account` secret. The script prints the service account and cloud project it is using at the start of each run.

### PlayFab: `upload_playfab_items.py`
```
python upload_playfab_items.py --input .\playfab --dry-run
python upload_playfab_items.py --input .\playfab --only wrestler_jazz
python upload_playfab_items.py --input .\playfab
python upload_playfab_items.py --input .\playfab --publish
```
Options: `--input` (file or folder; every `.json` in it, including subfolders, is read, so keep other JSON out), `--vault`, `--only friendly_id ...`, `--publish`.

---

## 7. What happens when an item already exists

Nothing fails just because an item exists. The scripts create it if it's new, and update it if it isn't.

| Platform | New item | Item already exists |
|---|---|---|
| Apple product | created | looked up by product ID; name / review note / family sharing are updated if they differ |
| Apple localization | created | looked up by locale; name and description are updated if they differ |
| Apple price and availability | sent | sent again; if Apple answers "already exists" (409) it is noted as left unchanged |
| Apple review screenshot | reserved, uploaded, committed | the existing screenshot is deleted and the new one uploaded. If Apple won't let the old one be deleted, it is kept, the screenshot steps are skipped, and a note is added (no failure) |
| Google product | created | updated (the requests use `allowMissing`) |
| PlayFab item | created as a draft | found by FriendlyId and updated |

Things that can't be changed after creation: Apple's product ID and product type, and Google's product ID. To rename a product ID, create a new product.

The first real error (anything that isn't one of the "already exists" cases above) stops the run.

## 8. Summary and log files

At the end of each script, and so at the end of a wrapper run, you get:

```
===== Apple summary =====
Created new (2):
  - 0001 (wrestler_new)
Already existed, updated (1):
  - 0002 (wrestler_jazz)
Notes (1):
  - 0002 (wrestler_jazz): 04-availability.json: already set, left unchanged
```

If something fails, there are also **FAILED** and **Not processed** sections. All of it is also written to the log file, with the time on every line. Wrapper runs create `logs\upload_all_<date>_<time>.log` containing all three platforms. Standalone runs create `logs\<platform>_<date>_<time>.log`.

Apple note: a product that already exists is listed under "updated" even if none of its values changed. The detail lines above the summary say which parts changed (for example "already existed, already up to date").

---

## 9. Troubleshooting

| Problem | Fix |
|---|---|
| Apple `409 ... This product ID has already been used` | Apple never lets a product ID be reused, even after the product was deleted. Use a new product ID. Existing products still on the app are fine; they're updated. |
| Apple locale rejected | Use `en-US`, not `en` (also `en-GB`, `en-AU`, `en-CA`, `fr-FR`, `de-DE`, ...). |
| Apple screenshot rejected for size | Use a screenshot of an accepted size (e.g. 1320 x 2868 for iPhone 6.9-inch). The script stops with Apple's error message. |
| Google 403 "API has not been used in project ..." | Enable the Google Play Android Developer API in the project named in the error, which should be the project shown in the "cloud project" line the script prints. If that's the wrong project, you're using the wrong key file. |
| Google 403 "permission denied" after enabling the API | Invite the service account's email in Play Console > Users and permissions, with access to the app. It can take a while to take effect. |
| PlayFab can't authenticate | Check the `playfab-title-id` and `playfab-secret-key` secrets in the vault. |
| "Not signed in to Azure" | Run `az login` or click **Sign in to Azure** in the window. Sign-in expires after a while; just sign in again. |
| "Your account can't read secrets in vault ..." | You need the **Key Vault Secrets User** role on the vault (ask the vault owner). It can take a few minutes to apply. If the vault has a firewall, your network must be allowed. |
| "Secret '...' was not found" | The secret name in the vault differs from section 3a. |
| "Could not reach https://....vault.azure.net" | The `keyvault_name` is wrong (use the name only, not the URL), or there is no internet connection. |
| `az` not found / "Azure CLI not installed" | Install it (`winget install Microsoft.AzureCLI`), then close and reopen the terminal or the window. |
| Sign-in works in the browser but access fails for a guest account | Sign in with `az login --tenant <the vault's tenant>`, or set `azure_tenant` in the config. |
| `ModuleNotFoundError` (including `azure`) | Re-run the `pip install` line in section 1. |
| An old `.state` folder inside the Apple root | No longer used. You can delete it. |

Remember that the IDs must be consistent across stores in whatever your game reads: Apple and Google product IDs may differ, but your game (or PlayFab) needs to map each one to the same item.
