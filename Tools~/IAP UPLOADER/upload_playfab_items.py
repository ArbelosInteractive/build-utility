#!/usr/bin/env python3
r"""Upload PlayFab Economy v2 catalog items from per-product JSON files.

Each file is the body of a Catalog/CreateDraftItem call:

    {"Publish": false, "Item": {"Type": "catalogItem", "ContentType": "wrestler",
     "Title": {...}, "Description": {...},
     "AlternateIds": [{"Type": "FriendlyId", "Value": "wrestler_jazz"}]}}

Re-run safe: for each file the script first looks the item up by its FriendlyId. If it
already exists (as a draft) it is updated with UpdateDraftItem, otherwise it is created
with CreateDraftItem. Nothing is saved locally; PlayFab is the source of truth.
The first error stops the run. A summary of created / updated items is printed and written
to a log file (logs\playfab_<date>_<time>.log, or --log-file).

Credentials: read from Azure Key Vault (secrets playfab-title-id, playfab-secret-key).
             The vault name comes from upload_config.json or --vault; sign in with `az login`.
Install:     pip install requests azure-identity azure-keyvault-secrets
Usage:       python upload_playfab_items.py --input .\playfab --dry-run
             python upload_playfab_items.py --input .\playfab
             python upload_playfab_items.py --input .\playfab --only wrestler_jazz
             python upload_playfab_items.py --input .\playfab --publish

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
from iap_secrets import PLAYFAB_SECRET_KEY, PLAYFAB_TITLE_ID, SecretError, Vault

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

    def find_draft(self, friendly_id):
        try:
            return self.call("/Catalog/GetDraftItem",
                             {"AlternateId": {"Type": ALT_TYPE, "Value": friendly_id}})["Item"]
        except ApiError as e:
            if e.not_found:
                return None
            raise


# ---------------------------------------------------------------- loading
def load_credentials(vault):
    """Read the title ID and secret key from Key Vault."""
    return {"title_id": vault.get_text(PLAYFAB_TITLE_ID),
            "secret_key": vault.get_text(PLAYFAB_SECRET_KEY)}


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
    ap.add_argument("--dry-run", action="store_true", help="validate and print, call nothing")
    args = ap.parse_args()
    setup_logging("playfab", args.log_file)

    if not args.input.exists():
        sys.exit(f"Input not found: {args.input}")
    items, problems = collect(args.input)
    if args.only:
        items = [i for i in items if i[1] in args.only]
    if problems:
        print("Problems found, nothing was uploaded:")
        for p in problems:
            print(f"  - {p}")
        sys.exit(1)
    if not items:
        sys.exit("No items to upload.")

    if args.dry_run:
        for f, fid, body in items:
            item = body["Item"]
            print(f"  {fid:<28} {item['ContentType']:<10} \"{item['Title']['NEUTRAL']}\"   ({f.name})"
                  + ("  [publish]" if args.publish else ""))
        print(f"\nDry run: {len(items)} item(s) valid, nothing was sent.")
        return

    try:
        creds = load_credentials(Vault(args.vault))
    except SecretError as e:
        sys.exit(str(e))
    client = PlayFab(creds["title_id"], creds["secret_key"])
    print(f"Title {creds['title_id']}")
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
                body["Item"]["Id"] = existing["Id"]
                client.call("/Catalog/UpdateDraftItem", body)
                report.updated.append(fid)
                print(f"  {fid}: already existed, values updated (id {existing['Id']})")
            else:
                resp = client.call("/Catalog/CreateDraftItem", body)
                report.created.append(fid)
                print(f"  {fid}: created new (id {(resp.get('Item') or {}).get('Id', '?')})")
        except ApiError as e:  # the first failure stops the run
            print(f"  FAILED: {fid} ({f.name}): {e}")
            report.failed.append(f"{fid} ({f.name}): {e}")
            report.not_run = [i[1] for i in items[n + 1:]]
            report.print()
            sys.exit(1)

    if not args.publish:
        report.notes.append("items are drafts; use --publish or publish them in Game Manager")
    report.print()


if __name__ == "__main__":
    main()
