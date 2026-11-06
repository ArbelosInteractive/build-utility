#!/usr/bin/env python3
r"""Upload PlayFab Economy v2 catalog items from per-product JSON files.

Each file is the body of a Catalog/CreateDraftItem call:

    {"Publish": false, "Item": {"Type": "catalogItem", "ContentType": "wrestler",
     "Title": {...}, "Description": {...},
     "AlternateIds": [{"Type": "FriendlyId", "Value": "wrestler_jazz"}]}}

Re-run safe: for each file the script first looks the item up by its FriendlyId. If it
already exists (as a draft) it is updated with UpdateDraftItem, otherwise it is created
with CreateDraftItem. Nothing is saved locally; PlayFab is the source of truth.

Updating merges: the file's fields replace the draft's, and everything the file leaves out
keeps its current PlayFab value (images, StartDate/EndDate, IsHidden, tags, other
DisplayProperties such as the thumbnail metadata). The draft's ETag is sent along, so a draft
changed by someone else in the meantime makes the update fail instead of being overwritten.
Release-wave dates come from the export: a file with StartDate/EndDate sets them, a file
without keeps the dates the item already has. A file whose DisplayProperties.dlcPump names a
different wave schedule than the draft's fails, like the DLC PlayFab Review window does.
The first error stops the run. A summary of created / updated items is printed and written
to a log file (logs\playfab_<date>_<time>.log, or --log-file).

Targets: --target staging (the default) uploads to the staging title, --target production to the
real one. Every item a staging upload sends is recorded in playfab-staging-receipt.json next to
the input folder (the export root), with a hash of its file. A production upload only runs when
every selected item is in that receipt with the same hash, i.e. exactly this file went to staging
first. --skip-staging-check turns the check off (for items that were tested some other way).

Icons: with --icons FOLDER every item gets a Thumbnail image: FOLDER/<FriendlyId>.png, else
FOLDER/default_<ContentType>.png (see iap_icons.py). An item with neither stops the run before anything is sent.
The icon is only uploaded when the item doesn't already show it (its hash is in DisplayProperties.dlcPump).

Credentials: read from Azure Key Vault. Staging: playfab-staging-title-id, playfab-staging-secret-key;
             production: playfab-title-id, playfab-secret-key.
             The vault name comes from upload_config.json or --vault; sign in with `az login`.
Install:     pip install requests azure-identity azure-keyvault-secrets
Usage:       python upload_playfab_items.py --input .\playfab --dry-run
             python upload_playfab_items.py --input .\playfab --publish                     (staging)
             python upload_playfab_items.py --input .\playfab --only wrestler_jazz
             python upload_playfab_items.py --input .\playfab --icons ..\..\Assets\Editor\StoreIcons --publish
             python upload_playfab_items.py --input .\playfab --target production --publish

Items stay as drafts unless you pass --publish (which sets "Publish": true on each call).
"""
import argparse
import json
import os
import sys
import time
from pathlib import Path

import requests

from iap_common import Report, setup_logging
from iap_icons import attach_icon, has_icon
from iap_secrets import PLAYFAB_TARGETS, SecretError, Vault
from iap_staging import receipt_path, record_staging, untested

ALT_TYPE = "FriendlyId"


class ApiError(Exception):
    def __init__(self, status, body):
        self.status = status
        try:
            self.data = json.loads(body)
        except ValueError:
            self.data = {}
        self.error_code = self.data.get("errorCode")
        self.error_name = self.data.get("error")
        super().__init__(f"HTTP {status}: {body}")

    @property
    def not_found(self):
        return self.error_code == 1047 or self.error_name == "ItemNotFound"


class PlayFab:
    def __init__(self, title_id, secret_key):
        self.base = f"https://{title_id}.playfabapi.com"
        self.secret = secret_key
        self.session = requests.Session()
        self.token = None

    def _post(self, path, body, headers):
        for attempt in range(4):
            r = self.session.post(self.base + path, json=body, headers=headers, timeout=60)
            if r.status_code in (429, 500, 502, 503, 504) and attempt < 3:
                time.sleep(float(r.headers.get("Retry-After", 2 ** attempt)))
                continue
            break
        if not r.ok:
            raise ApiError(r.status_code, r.text)
        return r.json()

    def login(self):
        """Exchange the title secret key for a title entity token."""
        resp = self._post("/Authentication/GetEntityToken", {"CustomTags": {"tool": "iap-upload"}},
                          {"X-SecretKey": self.secret})
        self.token = resp["data"]["EntityToken"]

    def call(self, path, body):
        return self._post(path, body, {"X-EntityToken": self.token})["data"]

    def upload_image(self, icon):
        """Uploads an icon PNG to PlayFab's catalog content storage; returns the Thumbnail image entry."""
        urls = self.call("/Catalog/CreateUploadUrls", {"Files": [{"FileName": f"icon-{icon.sha}.png"}]}).get("UploadUrls") or []
        if not urls or not urls[0].get("Url") or not urls[0].get("Id"):
            raise ValueError("PlayFab returned no image upload URL")
        url, image_id = urls[0]["Url"], urls[0]["Id"]
        for attempt in range(4):
            r = self.session.put(url, data=icon.data, timeout=120,
                                 headers={"x-ms-blob-type": "BlockBlob", "Content-Type": "image/png"})
            if r.status_code in (429, 500, 502, 503, 504) and attempt < 3:
                time.sleep(2 ** attempt)
                continue
            break
        if not r.ok:
            raise ValueError(f"icon upload of {icon.path.name} failed (HTTP {r.status_code})")
        return {"Id": image_id, "Type": "Thumbnail", "Url": url.split("?", 1)[0]}

    def find_draft(self, friendly_id):
        try:
            return self.call("/Catalog/GetDraftItem",
                             {"AlternateId": {"Type": ALT_TYPE, "Value": friendly_id}})["Item"]
        except ApiError as e:
            if e.not_found:
                return None
            raise


# ---------------------------------------------------------------- loading
def load_credentials(vault, target="production"):
    """Read the target's title ID and secret key from Key Vault."""
    title_secret, key_secret = PLAYFAB_TARGETS[target]
    return {"title_id": vault.get_text(title_secret), "secret_key": vault.get_text(key_secret)}


def friendly_id(body):
    for alt in (body.get("Item") or {}).get("AlternateIds") or []:
        if alt.get("Type") == ALT_TYPE:
            return alt.get("Value")
    return None


def validate(body):
    errs = []
    item = body.get("Item")
    if not isinstance(item, dict):
        return ["missing 'Item' object"]
    if not item.get("Type"):
        errs.append("Item.Type is missing")
    if not item.get("ContentType"):
        errs.append("Item.ContentType is missing")
    if not (item.get("Title") or {}).get("NEUTRAL"):
        errs.append("Item.Title.NEUTRAL is missing (PlayFab's default/fallback locale)")
    if not friendly_id(body):
        errs.append(f"no {ALT_TYPE} in Item.AlternateIds")
    if item.get("Id"):
        errs.append("Item.Id is set; leave it out, PlayFab assigns it (the script finds it by FriendlyId)")
    return errs


READ_ONLY = ("CreationDate", "LastModifiedDate", "Moderation", "Rating", "CreatorEntity", "FriendlyId")


def merge_existing(existing, item):
    """The draft to send with UpdateDraftItem: the existing draft, with the export file's fields on top.

    Fields the file doesn't set keep their PlayFab values. DisplayProperties are merged key by key, and so is
    DisplayProperties.dlcPump (so the thumbnail hash and pixel-art flag set by the Review window survive).
    Raises ValueError when the file and the draft are managed by different wave schedules.
    """
    merged = {k: v for k, v in existing.items() if k not in READ_ONLY}
    for key, value in item.items():
        old = merged.get(key)
        if key == "DisplayProperties" and isinstance(value, dict) and isinstance(old, dict):
            props = dict(old)
            for pkey, pvalue in value.items():
                if pkey == "dlcPump" and isinstance(pvalue, dict) and isinstance(props.get(pkey), dict):
                    mine, theirs = pvalue.get("scheduleId"), props[pkey].get("scheduleId")
                    if mine and theirs and mine != theirs:
                        raise ValueError(f"the item is managed by wave schedule {theirs}, but the export uses {mine}. "
                                         "Resolve which schedule owns it before uploading.")
                    pump = dict(props[pkey])
                    pump.update(pvalue)
                    props[pkey] = pump
                else:
                    props[pkey] = pvalue
            merged[key] = props
        else:
            merged[key] = value
    merged["Id"] = existing["Id"]
    return merged


def set_thumbnail(client, item, existing, icon):
    """Puts the icon on the item to send as its Thumbnail (other images are kept). Returns a word for the log."""
    if has_icon(existing, icon):
        return "unchanged"
    images = [i for i in (item.get("Images") or []) if isinstance(i, dict) and i.get("Type") != "Thumbnail"]
    item["Images"] = images + [client.upload_image(icon)]
    return "uploaded"


def describe_dates(item):
    start, end = item.get("StartDate"), item.get("EndDate")
    if start is None and end is None and "IsHidden" not in item:
        return ""
    return f"  dates {start or '-'} -> {end or 'no end'}" + ("  [hidden]" if item.get("IsHidden") else "")


def collect(target):
    files = [target] if target.is_file() else sorted(target.rglob("*.json"))
    if not files:
        sys.exit(f"No .json files found in {target}")
    items, problems, seen = [], [], {}
    for f in files:
        try:
            body = json.loads(f.read_text(encoding="utf-8-sig"))
        except ValueError as e:
            problems.append(f"{f.name}: not valid JSON ({e})")
            continue
        if "credentials" in f.name.lower():
            continue
        errs = validate(body)
        fid = friendly_id(body)
        if fid in seen:
            errs.append(f"duplicate FriendlyId {fid!r} (also in {seen[fid]})")
        seen.setdefault(fid, f.name)
        problems += [f"{f.name}: {e}" for e in errs]
        items.append((f, fid, body))
    return items, problems


# ------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--input", type=Path, default=Path("playfab"),
                    help="a JSON file or a folder of them (default: .\\playfab)")
    ap.add_argument("--vault", help="Azure Key Vault name (default: keyvault_name in upload_config.json)")
    ap.add_argument("--only", nargs="*", metavar="FRIENDLY_ID", help="only these FriendlyIds")
    ap.add_argument("--log-file", type=Path, help="append to this log file instead of starting a new one")
    ap.add_argument("--publish", action="store_true",
                    help="publish each item immediately instead of leaving it as a draft")
    ap.add_argument("--target", choices=sorted(PLAYFAB_TARGETS), default="staging",
                    help="which PlayFab title to upload to (default: staging)")
    ap.add_argument("--skip-staging-check", action="store_true",
                    help="production only: upload even if items weren't uploaded to staging first")
    ap.add_argument("--icons", type=Path,
                    help="folder of icon PNGs: <FriendlyId>.png, else default_<ContentType>.png, uploaded as thumbnails")
    ap.add_argument("--dry-run", action="store_true", help="validate and print, call nothing")
    args = ap.parse_args()
    setup_logging("playfab", args.log_file)

    if not args.input.exists():
        sys.exit(f"Input not found: {args.input}")
    items, problems = collect(args.input)
    if args.only:
        items = [i for i in items if i[1] in args.only]
    icons = {}
    if args.icons:
        if not args.icons.is_dir():
            sys.exit(f"Icon folder not found: {args.icons}")
        for f, fid, body in items:
            if fid and isinstance(body.get("Item"), dict):
                try:
                    icons[fid] = attach_icon(body, args.icons)  # before the staging check: the icon is part of the hash
                except (OSError, ValueError) as e:
                    problems.append(f"{f.name}: {e}")
    if problems:
        print("Problems found, nothing was uploaded:")
        for p in problems:
            print(f"  - {p}")
        sys.exit(1)
    if not items:
        sys.exit("No items to upload.")

    receipt = receipt_path(args.input)
    print(f"Target: {args.target.upper()} PlayFab title")
    if args.target == "production":
        missing = untested(receipt, items)
        if missing and not args.skip_staging_check:
            print(f"Not tested on staging (receipt: {receipt}):")
            for fid, reason in missing:
                print(f"  - {fid}: {reason}")
            sys.exit(f"{len(missing)} of {len(items)} item(s) haven't been uploaded to the staging title as they are "
                     "now. Upload them to staging and test them first, or pass --skip-staging-check.")
        if missing:
            print(f"WARNING: {len(missing)} item(s) not tested on staging; uploading anyway (--skip-staging-check).")
        else:
            print(f"Staging check: all {len(items)} item(s) match their staging upload.")

    if args.dry_run:
        for f, fid, body in items:
            item = body["Item"]
            print(f"  {fid:<28} {item['ContentType']:<10} \"{item['Title']['NEUTRAL']}\"   ({f.name})"
                  + ("  [publish]" if args.publish else "") + describe_dates(item)
                  + (f"  icon {icons[fid].path.name}" if fid in icons else ""))
        print(f"\nDry run: {len(items)} item(s) valid, nothing was sent.")
        return

    try:
        creds = load_credentials(Vault(args.vault), args.target)
        if args.target == "production":
            staging_title = load_credentials(Vault(args.vault), "staging")["title_id"]
            if staging_title.lower() == creds["title_id"].lower():
                sys.exit("playfab-title-id and playfab-staging-title-id are the same title. "
                         "Fix the secrets in the vault before uploading.")
    except SecretError as e:
        sys.exit(str(e))
    client = PlayFab(creds["title_id"], creds["secret_key"])
    print(f"Title {creds['title_id']} ({args.target})")
    try:
        client.login()
    except ApiError as e:
        sys.exit(f"Could not authenticate: {e}\n  -> Check title_id and secret_key "
                 "(Game Manager > Settings > Secret keys), then update the playfab-* secrets in the vault.")

    report = Report("PlayFab")
    for n, (f, fid, body) in enumerate(items):
        body = json.loads(json.dumps(body))  # copy
        if args.publish:
            body["Publish"] = True
        try:
            existing = client.find_draft(fid)
            if existing:
                body["Item"] = merge_existing(existing, body["Item"])
            thumb = ""
            if fid in icons:
                thumb = f"  icon {icons[fid].path.name} {set_thumbnail(client, body['Item'], existing, icons[fid])}"
            if existing:
                client.call("/Catalog/UpdateDraftItem", body)
                report.updated.append(fid)
                print(f"  {fid}: already existed, values updated (id {existing['Id']}){describe_dates(body['Item'])}{thumb}")
            else:
                resp = client.call("/Catalog/CreateDraftItem", body)
                report.created.append(fid)
                print(f"  {fid}: created new (id {(resp.get('Item') or {}).get('Id', '?')}){describe_dates(body['Item'])}{thumb}")
            if args.target == "staging":
                record_staging(receipt, creds["title_id"], fid, items[n][2], args.publish)
        except (ApiError, ValueError) as e:  # the first failure stops the run
            print(f"  FAILED: {fid} ({f.name}): {e}")
            report.failed.append(f"{fid} ({f.name}): {e}")
            report.not_run = [i[1] for i in items[n + 1:]]
            report.print()
            sys.exit(1)

    if not args.publish:
        report.notes.append("items are drafts; use --publish or publish them in Game Manager")
    if args.target == "staging":
        report.notes.append(f"recorded in {receipt.name}; once tested, upload the same export with --target production")
    report.print()


if __name__ == "__main__":
    main()
