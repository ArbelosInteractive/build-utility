#!/usr/bin/env python3
r"""Upload Apple in-app purchases from per-product folders of JSON files.

Expected layout (one numbered folder per product):

    <root>/0001/00-pricepoint.json        {"territory": "USA", "customerPrice": "4.99"}
    <root>/0001/01-create.json
    <root>/0001/02-localization-01.json   (any number of 02-localization-*.json)
    <root>/0001/03-pricing.json
    <root>/0001/04-availability.json
    <root>/0001/05-screenshot-reserve.json
    <root>/0001/06-screenshot-commit.json
    <root>/0001/review-screenshot.png     (the file named by fileName in the reserve JSON)

Placeholders like {{apple-0001-create.data.id}} are replaced with values from the
API response of the earlier step in the same run (folder 0001 + file 01-create.json ->
"apple-0001-create"). {{apple-0001-pricepoints.selected.id}} comes from the
00-pricepoint.json lookup. Apple's own ${price1} local ids are left untouched.

Safe to run again. For each product:
  * the in-app purchase is created, or, if the product ID already exists, its name /
    familySharable / reviewNote are updated
  * each localization is created, or updated if that locale already exists
  * price schedule and availability are sent again; if Apple answers that they already
    exist (409) they are reported as left unchanged
  * the review screenshot: an existing one is deleted and the new one uploaded. If the old
    one can't be deleted, it is kept and the screenshot steps are skipped (no failure).
The first real error stops the run. A summary of created / updated items is printed and
written to a log file (logs\apple_<date>_<time>.log, or --log-file).

Credentials: read from Azure Key Vault (secrets apple-key-id, apple-issuer-id, apple-p8).
             The vault name comes from upload_config.json or --vault; sign in with `az login`.
Install:     pip install requests "pyjwt[crypto]" azure-identity azure-keyvault-secrets
Usage:       python upload_apple_iap.py --root .\apple --dry-run
             python upload_apple_iap.py --root .\apple --products 0001
"""
import argparse
import hashlib
import json
import os
import re
import sys
import time
from decimal import Decimal
from pathlib import Path

import jwt
import requests

from iap_common import Report, setup_logging
from iap_secrets import APPLE_ISSUER_ID, APPLE_KEY_ID, APPLE_P8, SecretError, Vault

BASE = "https://api.appstoreconnect.apple.com"
SCREENSHOT = "inAppPurchaseAppStoreReviewScreenshots"
ENDPOINTS = {
    "inAppPurchases": "/v2/inAppPurchases",
    "inAppPurchaseLocalizations": "/v1/inAppPurchaseLocalizations",
    "inAppPurchasePriceSchedules": "/v1/inAppPurchasePriceSchedules",
    "inAppPurchaseAvailabilities": "/v1/inAppPurchaseAvailabilities",
    SCREENSHOT: "/v1/inAppPurchaseAppStoreReviewScreenshots",
}
PLACEHOLDER = re.compile(r"\{\{([\w-]+)\.([^{}]+)\}\}")
IAP_UPDATABLE = ("name", "reviewNote", "familySharable")
LOCALIZATION_UPDATABLE = ("name", "description")


class ApiError(Exception):
    def __init__(self, status, body):
        super().__init__(f"HTTP {status}: {body}")
        self.status = status


class PlaceholderError(Exception):
    pass


class Client:
    def __init__(self, key_id, issuer_id, key):
        self.key_id, self.issuer_id = key_id, issuer_id
        self.key = key  # the text of the .p8 file
        self._token, self._exp = None, 0
        self.session = requests.Session()

    def _auth(self):
        now = int(time.time())
        if now >= self._exp - 60:
            self._exp = now + 15 * 60
            self._token = jwt.encode(
                {"iss": self.issuer_id, "iat": now, "exp": self._exp, "aud": "appstoreconnect-v1"},
                self.key, algorithm="ES256", headers={"kid": self.key_id},
            )
        return {"Authorization": f"Bearer {self._token}"}

    def request(self, method, path, body=None, params=None):
        url = path if path.startswith("http") else BASE + path
        for attempt in range(4):
            r = self.session.request(method, url, headers=self._auth(), json=body,
                                     params=params, timeout=60)
            if r.status_code in (429, 500, 502, 503, 504) and attempt < 3:
                time.sleep(float(r.headers.get("Retry-After", 2 ** attempt)))
                continue
            break
        if not r.ok:
            raise ApiError(r.status_code, r.text)
        return r.json() if r.content else {}


# ----------------------------------------------------------------- credentials
def load_credentials(vault):
    """Read the key ID, issuer ID and .p8 text from Key Vault."""
    creds = {"key_id": vault.get_text(APPLE_KEY_ID),
             "issuer_id": vault.get_text(APPLE_ISSUER_ID),
             "key": vault.get(APPLE_P8)}
    if "BEGIN PRIVATE KEY" not in creds["key"]:
        raise SecretError(f"Secret {APPLE_P8!r} doesn't look like a .p8 file "
                          "(it should start with -----BEGIN PRIVATE KEY-----).")
    return creds


# ---------------------------------------------------------------- placeholders
def lookup(ctx, name, path):
    if name not in ctx:
        raise PlaceholderError("{{" + f"{name}.{path}" + "}}: no stored response for "
                               f"'{name}' (check file names and order)")
    value = ctx[name]
    try:
        for part in path.split("."):
            value = value[int(part)] if isinstance(value, list) else value[part]
    except (KeyError, IndexError, ValueError, TypeError) as e:
        raise PlaceholderError("{{" + f"{name}.{path}" + "}}: path not found in response "
                               f"({e!r})")
    return value


def resolve(text, ctx, strict=True):
    def sub(m):
        try:
            return str(lookup(ctx, m.group(1), m.group(2)))
        except PlaceholderError:
            if strict:
                raise
            return m.group(0)
    return PLACEHOLDER.sub(sub, text)


# ------------------------------------------------------------------ API helpers
def paged(client, path, params=None):
    while path:
        page = client.request("GET", path, params=params)
        yield from page.get("data", [])
        path, params = page.get("links", {}).get("next"), None


def find_existing_iap(client, cache, app_id, product_id):
    if app_id not in cache:
        cache[app_id] = list(paged(client, f"/v1/apps/{app_id}/inAppPurchasesV2", {"limit": 200}))
    return next((i for i in cache[app_id]
                 if i.get("attributes", {}).get("productId") == product_id), None)


def find_price_point(client, iap_id, spec):
    target = Decimal(str(spec["customerPrice"]))
    params = {"filter[territory]": spec.get("territory", "USA"), "limit": 200}
    for item in paged(client, f"/v2/inAppPurchases/{iap_id}/pricePoints", params):
        if Decimal(item["attributes"]["customerPrice"]) == target:
            return {"id": item["id"], "customerPrice": item["attributes"]["customerPrice"]}
    raise RuntimeError(f"No price point of {target} in {params['filter[territory]']}; "
                       "it must be one of Apple's price tiers")


def md5(path):
    return hashlib.md5(path.read_bytes()).hexdigest()


def upload_asset(reservation, image):
    """Send the file to the URLs from the reserve response (no bearer token here)."""
    blob = image.read_bytes()
    for op in reservation["data"]["attributes"]["uploadOperations"]:
        headers = {h["name"]: h["value"] for h in op.get("requestHeaders", [])}
        chunk = blob[op["offset"]: op["offset"] + op["length"]]
        r = requests.request(op["method"], op["url"], headers=headers, data=chunk, timeout=120)
        r.raise_for_status()


def wait_for_screenshot(client, shot_id, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        attrs = client.request("GET", f"{ENDPOINTS[SCREENSHOT]}/{shot_id}")["data"]["attributes"]
        delivery = attrs.get("assetDeliveryState") or {}
        if delivery.get("state") == "COMPLETE":
            return
        if delivery.get("state") == "FAILED":
            raise RuntimeError(f"Apple rejected the screenshot: {delivery.get('errors')}")
        time.sleep(3)
    print("    warning: screenshot still processing after timeout; check App Store Connect")


def find_screenshot_file(folder, ctx, shot_id):
    for value in ctx.values():
        data = value.get("data") if isinstance(value, dict) else None
        if isinstance(data, dict) and data.get("id") == shot_id:
            name = (data.get("attributes") or {}).get("fileName")
            if name:
                return folder / name
    return None


# ----------------------------------------------------------- create-or-update steps
def upsert_iap(client, cache, body, data):
    """Returns (action, response); action is created / updated / unchanged."""
    attrs = data["attributes"]
    app_id = data["relationships"]["app"]["data"]["id"]
    existing = find_existing_iap(client, cache, app_id, attrs["productId"])
    if not existing:
        return "created", client.request("POST", ENDPOINTS["inAppPurchases"], body)
    have = existing.get("attributes", {})
    patch = {k: attrs[k] for k in IAP_UPDATABLE if k in attrs and have.get(k) != attrs[k]}
    if not patch:
        return "unchanged", {"data": existing}
    resp = client.request("PATCH", f"{ENDPOINTS['inAppPurchases']}/{existing['id']}",
                          {"data": {"type": "inAppPurchases", "id": existing["id"], "attributes": patch}})
    return "updated", resp


def upsert_localization(client, iap_existed, body, data):
    attrs = data["attributes"]
    iap_id = data["relationships"]["inAppPurchaseV2"]["data"]["id"]
    if iap_existed:  # a brand-new product can't have localizations yet
        for loc in paged(client, f"/v2/inAppPurchases/{iap_id}/inAppPurchaseLocalizations", {"limit": 200}):
            have = loc.get("attributes", {})
            if have.get("locale") != attrs["locale"]:
                continue
            patch = {k: attrs[k] for k in LOCALIZATION_UPDATABLE if k in attrs and have.get(k) != attrs[k]}
            if not patch:
                return "unchanged", {"data": loc}
            resp = client.request("PATCH", f"{ENDPOINTS['inAppPurchaseLocalizations']}/{loc['id']}",
                                  {"data": {"type": "inAppPurchaseLocalizations", "id": loc["id"],
                                            "attributes": patch}})
            return "updated", resp
    return "created", client.request("POST", ENDPOINTS["inAppPurchaseLocalizations"], body)


def remove_existing_screenshot(client, iap_id):
    """Delete the product's current review screenshot.

    Returns None if there was none, True if it was deleted, False if it couldn't be deleted.
    """
    try:
        current = client.request("GET", f"/v2/inAppPurchases/{iap_id}/appStoreReviewScreenshot")
    except ApiError as e:
        if e.status == 404:
            return None
        raise
    shot = current.get("data")
    if not shot:
        return None
    try:
        client.request("DELETE", f"{ENDPOINTS[SCREENSHOT]}/{shot['id']}")
    except ApiError as e:
        print(f"    could not delete the existing screenshot ({e})")
        return False
    return True


# ---------------------------------------------------------------- per product
def process_product(client, folder, args, app_cache, info):
    """Runs every step for one product folder. `info` is filled in as it goes."""
    pid = folder.name
    ctx = {}
    create_key, price_key = f"apple-{pid}-create", f"apple-{pid}-pricepoints"
    spec_file = next(iter(sorted(folder.glob("*-pricepoint.json"))), None)
    steps = [p for p in sorted(folder.glob("[0-9][0-9]-*.json"))
             if not p.stem.endswith("pricepoint")]

    for path in steps:
        key = f"apple-{pid}-" + re.sub(r"^\d+-", "", path.stem)
        label = f"  [{pid}] {path.name}"
        text = path.read_text(encoding="utf-8-sig")

        # Look up the price point the first time a file needs it.
        if ("{{" + price_key + ".") in text and price_key not in ctx:
            if spec_file is None:
                raise RuntimeError(f"{path.name} needs {price_key} but there is no "
                                   f"*-pricepoint.json in {folder}")
            spec = json.loads(spec_file.read_text(encoding="utf-8-sig"))
            if args.dry_run:
                print(f"  [{pid}] would look up price point {spec} for {price_key}")
            else:
                ctx[price_key] = {"selected": find_price_point(
                    client, ctx[create_key]["data"]["id"], spec)}
                print(f"  [{pid}] price point for {spec['customerPrice']}: "
                      f"{ctx[price_key]['selected']['id']}")

        strict = not (args.dry_run or info["skip_shot"])
        body = json.loads(resolve(text, ctx, strict=strict))
        data = body["data"]
        dtype, is_patch = data["type"], "id" in data

        if dtype == SCREENSHOT and info["skip_shot"]:
            print(f"{label}: skipped (existing screenshot kept)")
            continue

        image = None
        if dtype == SCREENSHOT and not is_patch:  # reserve: use the real file size
            image = folder / data["attributes"]["fileName"]
            if not image.exists():
                raise FileNotFoundError(f"{image} (named in {path.name}) not found")
            data["attributes"]["fileSize"] = image.stat().st_size
        elif dtype == SCREENSHOT and is_patch:  # commit: use the real checksum
            image = find_screenshot_file(folder, ctx, data["id"])
            if image is not None:
                data["attributes"]["sourceFileChecksum"] = md5(image)
            elif not args.dry_run:
                raise RuntimeError(f"Cannot find the image for screenshot {data['id']}")

        if args.dry_run:
            action = "PATCH" if is_patch else "POST"
            extra = " (create, or update if it exists)" if dtype in (
                "inAppPurchases", "inAppPurchaseLocalizations") else ""
            print(f"{label}: would {action} {ENDPOINTS[dtype]}{extra}")
            if args.verbose:
                print(json.dumps(body, indent=2))
            continue

        # ---- real run
        if dtype == "inAppPurchases" and not is_patch:
            action, resp = upsert_iap(client, app_cache, body, data)
            ctx[key] = resp
            info["product_id"] = data["attributes"]["productId"]
            info["existed"] = action != "created"
            info["action"] = "created" if action == "created" else "updated"
            print(f"{label}: product {info['product_id']} "
                  + {"created": "created new", "updated": "already existed, values updated",
                     "unchanged": "already existed, values already up to date"}[action])

        elif dtype == "inAppPurchaseLocalizations" and not is_patch:
            action, resp = upsert_localization(client, info["existed"], body, data)
            ctx[key] = resp
            print(f"{label}: localization {data['attributes']['locale']} "
                  + {"created": "created new", "updated": "already existed, updated",
                     "unchanged": "already existed, already up to date"}[action])

        elif dtype == SCREENSHOT and not is_patch:
            iap_id = ctx[create_key]["data"]["id"]
            if info["existed"]:
                removed = remove_existing_screenshot(client, iap_id)
                if removed is False:
                    info["skip_shot"] = True
                    info["notes"].append("existing screenshot could not be deleted, kept as is")
                    print(f"{label}: existing screenshot kept, skipping the screenshot steps")
                    continue
                if removed:
                    print(f"{label}: existing screenshot deleted, uploading the new one")
            try:
                resp = client.request("POST", ENDPOINTS[SCREENSHOT], body)
            except ApiError as e:
                if e.status != 409:
                    raise
                info["skip_shot"] = True
                info["notes"].append("a screenshot already exists, kept as is")
                print(f"{label}: a screenshot already exists, keeping it and skipping")
                continue
            ctx[key] = resp
            upload_asset(resp, image)
            print(f"{label}: screenshot reserved and uploaded")

        elif dtype == SCREENSHOT and is_patch:
            client.request("PATCH", f"{ENDPOINTS[SCREENSHOT]}/{data['id']}", body)
            wait_for_screenshot(client, data["id"])
            print(f"{label}: screenshot committed")

        else:  # price schedule, availability, anything else
            method = "PATCH" if is_patch else "POST"
            url = ENDPOINTS[dtype] + (f"/{data['id']}" if is_patch else "")
            try:
                ctx[key] = client.request(method, url, body)
                print(f"{label}: {method} ok")
            except ApiError as e:
                if e.status == 409 and not is_patch:
                    info["notes"].append(f"{path.name}: already set, left unchanged")
                    print(f"{label}: already exists, left unchanged")
                else:
                    raise


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--root", type=Path, default=Path("."), help="folder containing 0001, 0002, ...")
    ap.add_argument("--products", nargs="*", help="only these folders, e.g. 0001 0002")
    ap.add_argument("--vault", help="Azure Key Vault name (default: keyvault_name in upload_config.json)")
    ap.add_argument("--log-file", type=Path, help="append to this log file instead of starting a new one")
    ap.add_argument("--dry-run", action="store_true", help="print what would be sent, call nothing")
    ap.add_argument("-v", "--verbose", action="store_true", help="print payloads in dry-run")
    args = ap.parse_args()
    setup_logging("apple", args.log_file)

    folders = sorted(p for p in args.root.iterdir() if p.is_dir() and re.fullmatch(r"\d{4}", p.name))
    if args.products:
        folders = [f for f in folders if f.name in args.products]
    if not folders:
        sys.exit("No product folders found.")

    client = None
    if not args.dry_run:
        try:
            creds = load_credentials(Vault(args.vault))
        except SecretError as e:
            sys.exit(str(e))
        client = Client(creds["key_id"], creds["issuer_id"], creds["key"])

    report, app_cache = Report("Apple"), {}
    for i, folder in enumerate(folders):
        print(f"== {folder.name}")
        info = {"existed": False, "action": None, "skip_shot": False, "notes": [],
                "product_id": folder.name}
        try:
            process_product(client, folder, args, app_cache, info)
        except Exception as e:  # the first failure stops the whole run
            print(f"  FAILED: {e}")
            detail = f"{folder.name} ({info['product_id']}): {e}"
            if info["action"]:
                detail += f"  [product was {info['action']} before the failure]"
            report.failed.append(detail)
            report.not_run = [f.name for f in folders[i + 1:]]
            report.print()
            sys.exit(1)
        name = f"{folder.name} ({info['product_id']})"
        if not args.dry_run:
            (report.created if info["action"] == "created" else report.updated).append(name)
            report.notes += [f"{name}: {n}" for n in info["notes"]]

    if args.dry_run:
        print("\nDry run: nothing was sent.")
    else:
        report.print()


if __name__ == "__main__":
    main()
