#!/usr/bin/env python3
r"""Upload Google Play one-time products from a batchUpdate JSON file.

The JSON file is the body of Google's monetization.onetimeproducts.batchUpdate call:

    {"requests": [ {"allowMissing": true, "updateMask": "listings,purchaseOptions",
                    "regionsVersion": {"version": "2022/02"},
                    "oneTimeProduct": {"packageName": ..., "productId": ..., ...}}, ... ]}

Because every request has allowMissing=true, a product that doesn't exist is created and
one that does is updated, so the script is safe to run again. Before uploading, the script
lists the products already in Play Console, so it can report which were created new and which
already existed and were updated. The first error stops the run. A summary is printed and
written to a log file (logs\google_<date>_<time>.log, or --log-file).

Credentials: the service account JSON, read from Azure Key Vault (secret
             google-service-account). The vault name comes from upload_config.json or
             --vault; sign in with `az login`.
Install:     pip install google-auth requests azure-identity azure-keyvault-secrets
Usage:       python upload_google_iap.py --input .\google --dry-run
             python upload_google_iap.py --input .\google
             python upload_google_iap.py --input .\google --activate
             python upload_google_iap.py --input .\google --only wrestler_jazz --batch-size 1

Without --activate, purchase options are left as drafts (not purchasable).
"""
import argparse
import json
import os
import re
import sys
import time
from collections import OrderedDict
from pathlib import Path

import requests  # noqa: F401  (needed by google-auth's transport)
from google.auth.transport.requests import AuthorizedSession
from google.oauth2 import service_account

from iap_common import Report, setup_logging
from iap_secrets import GOOGLE_SERVICE_ACCOUNT, SecretError, Vault

API = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications"
SCOPES = ["https://www.googleapis.com/auth/androidpublisher"]
MAX_BATCH = 100
PRODUCT_ID = re.compile(r"^[a-z0-9][a-z0-9_.]*$")
OPTION_ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")


class ApiError(Exception):
    HINTS = {
        401: "Authentication failed: check the service account key file.",
        403: "Permission denied: enable the 'Google Play Android Developer API' in the Google "
             "Cloud project, and invite the service account in Play Console > Users and "
             "permissions with access to this app (including its in-app products).",
        404: "Not found: check packageName, and that the service account has access to this app.",
    }

    def __init__(self, status, body):
        hint = self.HINTS.get(status)
        super().__init__(f"HTTP {status}: {body}" + (f"\n    -> {hint}" if hint else ""))
        self.status = status


# --------------------------------------------------------------------- loading
def load_requests(target):
    files = [target] if target.is_file() else sorted(target.glob("*.json"))
    if not files:
        sys.exit(f"No .json files found in {target}")
    found = []
    for f in files:
        data = json.loads(f.read_text(encoding="utf-8-sig"))
        if not isinstance(data, dict) or not isinstance(data.get("requests"), list):
            print(f"Skipping {f.name}: no 'requests' list")
            continue
        for i, req in enumerate(data["requests"], 1):
            found.append((f"{f.name} #{i}", req))
    return found


def validate(req):
    errs = []
    prod = req.get("oneTimeProduct") or {}
    pid = prod.get("productId")
    if not prod.get("packageName"):
        errs.append("missing oneTimeProduct.packageName")
    if not pid or not PRODUCT_ID.match(pid):
        errs.append(f"invalid productId {pid!r} (lowercase letters, digits, _ and . only; "
                    "must start with a letter or digit)")
    if not req.get("updateMask"):
        errs.append("missing updateMask")
    if not (req.get("regionsVersion") or {}).get("version"):
        errs.append("missing regionsVersion.version")

    listings = prod.get("listings") or []
    if not listings:
        errs.append("no listings")
    seen = set()
    for lst in listings:
        code = lst.get("languageCode")
        if code in seen:
            errs.append(f"duplicate listing for language {code}")
        seen.add(code)
        if len(lst.get("title", "")) > 55:
            errs.append(f"title for {code} is {len(lst['title'])} characters (max 55)")
        if len(lst.get("description", "")) > 200:
            errs.append(f"description for {code} is {len(lst['description'])} characters (max 200)")

    options = prod.get("purchaseOptions") or []
    if not options:
        errs.append("no purchaseOptions")
    for opt in options:
        oid = opt.get("purchaseOptionId")
        if not oid or not OPTION_ID.match(oid):
            errs.append(f"invalid purchaseOptionId {oid!r} (lowercase letters, digits and "
                        "hyphens only, max 63 characters)")
    return errs


def money(price):
    value = int(price.get("units", 0)) + int(price.get("nanos", 0)) / 1e9
    return f"{value:.2f} {price.get('currencyCode', '?')}"


def summarize(req):
    prod = req["oneTimeProduct"]
    title = (prod.get("listings") or [{}])[0].get("title", "")
    prices = []
    for opt in prod.get("purchaseOptions", []):
        for cfg in opt.get("regionalPricingAndAvailabilityConfigs", []):
            prices.append(f"{cfg.get('regionCode')} {money(cfg['price'])}" if "price" in cfg
                          else f"{cfg.get('regionCode')} (no price)")
    return f"{prod['productId']}  \"{title}\"  {', '.join(prices)}"


# ------------------------------------------------------------------------- API
def make_session(vault):
    try:
        info = json.loads(vault.get(GOOGLE_SERVICE_ACCOUNT))
    except ValueError:
        sys.exit(f"Secret {GOOGLE_SERVICE_ACCOUNT!r} isn't valid JSON. It should hold the whole "
                 "service account key file.")
    if info.get("type") != "service_account":
        sys.exit(f"Secret {GOOGLE_SERVICE_ACCOUNT!r} isn't a service account key "
                 f"(type is {info.get('type')!r}).")
    print(f"Using key from vault {vault.name}\n  service account: {info.get('client_email')}\n"
          f"  cloud project:   {info.get('project_id')}")
    creds = service_account.Credentials.from_service_account_info(info, scopes=SCOPES)
    return AuthorizedSession(creds)


def send(session, method, url, **kwargs):
    for attempt in range(4):
        r = session.request(method, url, timeout=120, **kwargs)
        if r.status_code in (429, 500, 502, 503, 504) and attempt < 3:
            time.sleep(float(r.headers.get("Retry-After", 2 ** attempt)))
            continue
        break
    if not r.ok:
        raise ApiError(r.status_code, r.text)
    return r.json()


def post(session, url, body):
    return send(session, "POST", url, json=body)


def existing_product_ids(session, package):
    """Product IDs that already exist in Play Console for this app."""
    ids, token = set(), None
    while True:
        data = send(session, "GET", f"{API}/{package}/oneTimeProducts",
                    params={"pageToken": token} if token else None)
        ids |= {p["productId"] for p in data.get("oneTimeProducts", [])}
        token = data.get("nextPageToken")
        if not token:
            return ids


def chunks(items, size):
    for i in range(0, len(items), size):
        yield items[i:i + size]


def describe(product):
    opts = ", ".join(f"{o.get('purchaseOptionId')}={o.get('state', '?')}"
                     for o in product.get("purchaseOptions", []))
    return f"{product.get('productId')}: {opts}"


def activate(session, package, products, batch_size):
    todo = [(p["productId"], o["purchaseOptionId"])
            for p in products for o in p.get("purchaseOptions", [])
            if o.get("state") != "ACTIVE"]
    if not todo:
        print("  nothing to activate")
        return
    for chunk in chunks(todo, batch_size):
        body = {"requests": [{"activatePurchaseOptionRequest": {
            "packageName": package, "productId": pid, "purchaseOptionId": oid}}
            for pid, oid in chunk]}
        resp = post(session, f"{API}/{package}/oneTimeProducts/-/purchaseOptions:batchUpdateStates", body)
        for product in resp.get("oneTimeProducts", []):
            print(f"  activated {describe(product)}")


# ------------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--input", type=Path, default=Path("google"),
                    help="a batch JSON file, or a folder of them (default: .\\google)")
    ap.add_argument("--vault", help="Azure Key Vault name (default: keyvault_name in upload_config.json)")
    ap.add_argument("--log-file", type=Path, help="append to this log file instead of starting a new one")
    ap.add_argument("--only", nargs="*", metavar="PRODUCT_ID", help="only these product IDs")
    ap.add_argument("--activate", action="store_true",
                    help="make the purchase options ACTIVE (purchasable) after uploading")
    ap.add_argument("--batch-size", type=int, default=MAX_BATCH, metavar="N",
                    help=f"products per API call, 1-{MAX_BATCH} (use 1 to pinpoint a failing product)")
    ap.add_argument("--dry-run", action="store_true", help="validate and print, call nothing")
    args = ap.parse_args()
    args.batch_size = max(1, min(args.batch_size, MAX_BATCH))
    setup_logging("google", args.log_file)

    if not args.input.exists():
        sys.exit(f"Input not found: {args.input}")

    # 1. load + validate
    groups, problems, seen = OrderedDict(), [], {}
    for label, req in load_requests(args.input):
        pid = (req.get("oneTimeProduct") or {}).get("productId")
        if args.only and pid not in args.only:
            continue
        errs = validate(req)
        pkg = (req.get("oneTimeProduct") or {}).get("packageName")
        key = (pkg, pid)
        if pid and key in seen:
            errs.append(f"duplicate of {seen[key]} (each product can appear only once)")
        seen.setdefault(key, label)
        if errs:
            problems += [f"{label} ({pid}): {e}" for e in errs]
        else:
            groups.setdefault(pkg, []).append((label, req))
    if problems:
        print("Problems found, nothing was uploaded:")
        for p in problems:
            print(f"  - {p}")
        sys.exit(1)
    total = sum(len(v) for v in groups.values())
    if not total:
        sys.exit("No products to upload.")

    # 2. dry run
    if args.dry_run:
        for pkg, items in groups.items():
            print(f"== {pkg}: {len(items)} product(s), {len(list(chunks(items, args.batch_size)))} call(s)")
            for _, req in items:
                print(f"  {summarize(req)}")
        print("\nDry run: validation passed, nothing was sent.")
        return

    # 3. upload (the first failure stops the run)
    try:
        session = make_session(Vault(args.vault))
    except SecretError as e:
        sys.exit(str(e))
    report = Report("Google Play")
    all_ids = [r["oneTimeProduct"]["productId"] for items in groups.values() for _, r in items]
    done = set()

    def fail(message):
        print(f"  FAILED: {message}")
        report.failed.append(message)
        report.not_run = [p for p in all_ids if p not in done]
        report.print()
        sys.exit(1)

    for pkg, items in groups.items():
        print(f"== {pkg}")
        try:
            existing = existing_product_ids(session, pkg)
        except ApiError as e:
            fail(f"could not list the existing products of {pkg}: {e}")
        uploaded = []
        for chunk in chunks(items, args.batch_size):
            names = ", ".join(r["oneTimeProduct"]["productId"] for _, r in chunk)
            try:
                resp = post(session, f"{API}/{pkg}/oneTimeProducts:batchUpdate",
                            {"requests": [r for _, r in chunk]})
            except ApiError as e:
                fail(f"upload of ({names}) failed, so none of these may have been saved: {e}")
            for _, req in chunk:
                pid = req["oneTimeProduct"]["productId"]
                if pid in existing:
                    report.updated.append(pid)
                    print(f"  {pid}: already existed, values updated")
                else:
                    report.created.append(pid)
                    print(f"  {pid}: created new")
                done.add(pid)
            for product in resp.get("oneTimeProducts", []):
                print(f"  state {describe(product)}")
                uploaded.append(product)
        if args.activate and uploaded:
            try:
                activate(session, pkg, uploaded, args.batch_size)
            except ApiError as e:
                fail(f"could not activate the purchase options: {e}")

    report.print()


if __name__ == "__main__":
    main()
