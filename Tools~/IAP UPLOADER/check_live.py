r"""Read-only check: is each product live everywhere a player needs it?

For every product it reports, side by side:

    google        Play Console one-time product: exists, purchase option ACTIVE, regions available
    apple         App Store Connect in-app purchase: state (APPROVED = live) and territories available
    pf-staging    PlayFab staging title: published, inside its start/end window, not hidden
    pf-prod       PlayFab production title: the same (n/a for consumables, which grant currency items)
    dlc-staging   listed in products.json for each platform in the staging container (non-consumables)
    dlc-prod      the same in the production container
    data-staging  the store will register it in staging builds: in the downloaded IAP catalog, or built into the app
    data-prod     the same for store builds

Each cell is OK, WARN (works but worth a look, e.g. a start date in the future) or FAIL (players can't buy or
receive it). "Live for players" means every production cell is OK. Nothing is ever written.

Containers are read through the same public URLs the game uses (DlcRemoteConfig), so CDN caching is included.
Store credentials come from Key Vault, like the uploaders.

Usage: python check_live.py --vault tsw-iap --catalog Assets/Resources/IAPProductCatalog.json \
           --apple-app-id 1622296866 --google-package com.example.game --app-version 1.4.0 \
           --staging-url https://<cdn>/dlc-staging --production-url https://<cdn>/dlc-production \
           [--platform Android iOS] [--product id1 id2 ...]
--progress prints one "@@live {json}" line per product (for the Unity window).
"""
import argparse
import datetime
import json
import sys
from pathlib import Path

OK, WARN, FAIL = "OK", "WARN", "FAIL"
UNPUBLISHED = "unpublished"  # no products.json for a platform: nothing published there yet
COLUMNS = ["google", "apple", "pf-staging", "pf-prod", "dlc-staging", "dlc-prod", "data-staging", "data-prod"]
PRODUCTION = ["google", "apple", "pf-prod", "dlc-prod", "data-prod"]
APPLE_LIVE = {"APPROVED"}
APPLE_PENDING = {"READY_TO_SUBMIT", "WAITING_FOR_REVIEW", "IN_REVIEW", "PENDING_BINARY_APPROVAL", "PROCESSING_CONTENT"}


def cell(status, detail=""):
    return {"status": status, "detail": detail}


def worst(cells):
    order = {OK: 0, WARN: 1, FAIL: 2}
    return max((c["status"] for c in cells), key=lambda s: order.get(s, 2), default=OK)


# ------------------------------------------------------------------ verdicts (pure, tested)

def google_cell(product):
    """product: the oneTimeProducts resource, or None when Play Console has no such product."""
    if product is None:
        return cell(FAIL, "not in Play Console")
    options = product.get("purchaseOptions") or []
    states = sorted({o.get("state", "?") for o in options})
    active = [o for o in options if o.get("state") == "ACTIVE"]
    if not active:
        return cell(FAIL, "purchase option " + ("/".join(states) if states else "missing") + " (not ACTIVE)")
    regions = {r.get("regionCode") for o in active for r in o.get("regionalPricingAndAvailabilityConfigs") or []
               if r.get("availability") in (None, "AVAILABLE")}
    if not regions:
        return cell(FAIL, "ACTIVE but no region available")
    return cell(OK, f"ACTIVE, {len(regions)} regions")


def apple_cell(iap, territories=None):
    """iap: the inAppPurchasesV2 resource or None; territories: count of available territories, or None if unknown."""
    if iap is None:
        return cell(FAIL, "not in App Store Connect")
    state = (iap.get("attributes") or {}).get("state", "?")
    where = "" if territories is None else f", {territories} territories"
    if state in APPLE_LIVE:
        return cell(FAIL, f"{state} but not available in any territory") if territories == 0 else cell(OK, state + where)
    if state in APPLE_PENDING:
        return cell(WARN, state + where)
    return cell(FAIL, state + where)


def parse_utc(value):
    """PlayFab dates: ISO 8601 with optional fractional seconds and zone. Naive values are UTC."""
    if not value:
        return None
    text = value.strip().replace("Z", "+00:00")
    main, zone = text, ""
    for sign in ("+", "-"):
        i = text.rfind(sign)
        if i > 10:
            main, zone = text[:i], text[i:]
            break
    main = main.split(".")[0]
    try:
        parsed = datetime.datetime.fromisoformat(main + (zone or "+00:00"))
    except ValueError:
        return None
    return parsed.astimezone(datetime.timezone.utc)


def playfab_cell(published, draft, now):
    if published is None:
        return cell(FAIL, "draft only, not published" if draft is not None else "not in the catalog")
    if published.get("IsHidden"):
        return cell(FAIL, "hidden")
    start, end = parse_utc(published.get("StartDate")), parse_utc(published.get("EndDate"))
    if end is not None and end <= now:
        return cell(FAIL, "ended " + end.strftime("%Y-%m-%d %H:%M UTC"))
    if start is not None and start > now:
        return cell(WARN, "starts " + start.strftime("%Y-%m-%d %H:%M UTC"))
    return cell(OK, "published" + (", ends " + end.strftime("%Y-%m-%d") if end else ""))


def content_cell(consumable, listed):
    """listed: {platform: True/False/None/UNPUBLISHED} (None = products.json unreadable, UNPUBLISHED = no
    products.json for that platform at all)."""
    if consumable:
        return cell(OK, "n/a (consumable)")
    unknown = [p for p, v in listed.items() if v is None]
    if unknown:
        return cell(WARN, "couldn't read products.json for " + ", ".join(unknown))
    empty = [p for p, v in listed.items() if v == UNPUBLISHED]
    published = {p: v for p, v in listed.items() if v != UNPUBLISHED}
    note = "; nothing published for " + ", ".join(empty) + " yet" if empty else ""
    missing = [p for p, v in published.items() if v is False]
    if not published:
        return cell(WARN, note[2:])
    if not missing:
        return cell(WARN if empty else OK, "listed for " + ", ".join(published) + note)
    if len(missing) == len(published):
        return cell(WARN, "not in products.json (fine only if its content ships in the app)" + note)
    return cell(FAIL, "missing for " + ", ".join(missing) + note)


def data_cell(product_id, built_in, remote):
    """remote: set of ids in the downloaded IAP catalog, or None when there is none for this app version."""
    if remote is not None and product_id in remote:
        return cell(OK, "in game data")
    if product_id in built_in:
        return cell(OK, "built into the app" + ("" if remote is None else " (not in game data)"))
    return cell(FAIL, "not in game data or the app: the store won't offer it" if remote is not None
                else "no game data published and not built in: the store won't offer it")


def catalog_products(text):
    """{id: type} from an IAPProductCatalog.json (type 0 = consumable)."""
    data = json.loads(text.lstrip(chr(0xFEFF)))
    return {p["id"]: p.get("type", 1) for p in data.get("products", []) if p.get("id")}


# ------------------------------------------------------------------ readers (network)

def http_get(url, timeout=20):
    import requests
    r = requests.get(url, timeout=timeout, headers={"Cache-Control": "no-cache"})
    if r.status_code == 404:
        return None
    r.raise_for_status()
    return r.text


def read_container(base, platforms, app_version, get=http_get):
    """({platform: set(productIds) or None}, set(ids in game data) or None, notes)."""
    listed, notes = {}, []
    for platform in platforms:
        try:
            text = get(f"{base}/{platform}/{app_version}/products.json")
            listed[platform] = UNPUBLISHED if text is None else {p["productId"] for p in json.loads(text).get("products", [])}
        except Exception as e:
            listed[platform] = None
            notes.append(f"{base}/{platform}/{app_version}/products.json: {e}")
    remote = None
    try:
        text = get(f"{base}/data/{app_version}/IAPProductCatalog.json")
        remote = None if text is None else set(catalog_products(text))
    except Exception as e:
        notes.append(f"{base}/data/{app_version}/IAPProductCatalog.json: {e}")
    return listed, remote, notes


class Stores:
    """Lazily signs in to each store once; a store that can't be reached reports every product as unknown."""

    def __init__(self, vault_name, apple_app_id, google_package):
        self.vault_name, self.apple_app_id, self.google_package = vault_name, apple_app_id, google_package
        self._vault = None
        self.errors = {}
        self._apple = self._google = None
        self._playfab = {}

    def vault(self):
        if self._vault is None:
            from iap_secrets import Vault
            self._vault = Vault(self.vault_name)
        return self._vault

    def _guard(self, key, fn):
        if key in self.errors:
            return None
        try:
            return fn()
        except SystemExit as e:
            self.errors[key] = str(e)
        except Exception as e:
            self.errors[key] = f"{type(e).__name__}: {str(e).strip().splitlines()[0] if str(e).strip() else ''}"
        return None

    def google(self, product_id):
        def run():
            from upload_google_iap import API, ApiError, make_session, send
            if self._google is None:
                if not self.google_package:
                    raise RuntimeError("no Google package name")
                self._google = make_session(self.vault())
            try:
                return ("found", send(self._google, "GET", f"{API}/{self.google_package}/oneTimeProducts/{product_id}"))
            except ApiError as e:
                if e.status == 404:
                    return ("found", None)
                raise
        return self._guard("google", run)

    def apple(self, product_id):
        def run():
            from upload_apple_iap import Client, load_credentials, paged
            if self._apple is None:
                if not self.apple_app_id:
                    raise RuntimeError("no Apple app ID")
                client = Client(**load_credentials(self.vault()))
                self._apple = (client, {i["attributes"]["productId"]: i for i in
                                        paged(client, f"/v1/apps/{self.apple_app_id}/inAppPurchasesV2", {"limit": 200})})
            client, iaps = self._apple
            iap = iaps.get(product_id)
            territories = None
            if iap is not None:
                try:
                    availability = client.request("GET", f"/v2/inAppPurchases/{iap['id']}/inAppPurchaseAvailability").get("data")
                    if availability:
                        territories = sum(1 for _ in paged(client, f"/v1/inAppPurchaseAvailabilities/{availability['id']}/availableTerritories", {"limit": 200}))
                except Exception:
                    territories = None
            return ("found", (iap, territories))
        return self._guard("apple", run)

    def playfab(self, target, product_id):
        def run():
            from upload_playfab_items import PlayFab, load_credentials
            if target not in self._playfab:
                client = PlayFab(**load_credentials(self.vault(), target))
                client.login()
                self._playfab[target] = client
            client = self._playfab[target]
            published = client.find_published(product_id)
            return ("found", (published, None if published is not None else client.find_draft(product_id)))
        return self._guard("playfab-" + target, run)


def unknown(key, stores):
    return cell(WARN, "couldn't check: " + stores.errors.get(key, "unknown error"))


# ------------------------------------------------------------------ main

def check(products, built_in, stores, staging, production, now=None):
    """products: {id: type}. staging/production: (listed per platform, remote ids, notes). Yields (id, cells)."""
    now = now or datetime.datetime.now(datetime.timezone.utc)
    for pid, ptype in products.items():
        consumable = ptype == 0
        cells = {}
        g = stores.google(pid)
        cells["google"] = google_cell(g[1]) if g else unknown("google", stores)
        a = stores.apple(pid)
        cells["apple"] = apple_cell(*a[1]) if a else unknown("apple", stores)
        for target, col in (("staging", "pf-staging"), ("production", "pf-prod")):
            if consumable:  # currency packs grant the PlayFab currency items, not an item with the product's ID
                cells[col] = cell(OK, "n/a (consumable)")
                continue
            r = stores.playfab(target, pid)
            cells[col] = playfab_cell(r[1][0], r[1][1], now) if r else unknown("playfab-" + target, stores)
        for (listed, remote, _), dlc, data in ((staging, "dlc-staging", "data-staging"), (production, "dlc-prod", "data-prod")):
            cells[dlc] = content_cell(consumable, {p: (ids if ids is None or ids == UNPUBLISHED else pid in ids)
                                                   for p, ids in listed.items()})
            cells[data] = data_cell(pid, built_in, remote)
        yield pid, cells


def main(argv=None):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--vault")
    ap.add_argument("--catalog", required=True, type=Path, help="the IAPProductCatalog.json built into the app")
    ap.add_argument("--apple-app-id", default="")
    ap.add_argument("--google-package", default="")
    ap.add_argument("--app-version", required=True)
    ap.add_argument("--staging-url", required=True)
    ap.add_argument("--production-url", required=True)
    ap.add_argument("--platform", nargs="+", default=["Android", "iOS"])
    ap.add_argument("--product", nargs="+", metavar="ID", help="default: every product in the remote or built-in catalog")
    ap.add_argument("--progress", action="store_true", help="print @@live JSON lines (for the Unity window)")
    args = ap.parse_args(argv)

    built = catalog_products(args.catalog.read_text(encoding="utf-8-sig"))
    print(f"Reading staging and production containers for {args.app_version} ({', '.join(args.platform)})...", flush=True)
    staging = read_container(args.staging_url.rstrip("/"), args.platform, args.app_version)
    production = read_container(args.production_url.rstrip("/"), args.platform, args.app_version)
    for note in staging[2] + production[2]:
        print("  note: " + note)

    known = dict(built)
    if args.product:
        products = {pid: known.get(pid, 1) for pid in args.product}
    else:
        products = known
        for remote in (staging[1], production[1]):
            for pid in sorted(remote or ()):
                products.setdefault(pid, 1)

    stores = Stores(args.vault, args.apple_app_id, args.google_package)
    width = max([len(p) for p in products] + [10])
    print("\n" + "product".ljust(width) + "  " + "  ".join(c[:12].ljust(12) for c in COLUMNS) + "  live")
    live_count = 0
    for pid, cells in check(products, set(built), stores, staging, production):
        live = all(cells[c]["status"] == OK for c in PRODUCTION)
        live_count += live
        print(pid.ljust(width) + "  " + "  ".join(cells[c]["status"].ljust(12) for c in COLUMNS) + "  " + ("YES" if live else "no"))
        for c in COLUMNS:
            if cells[c]["status"] != OK:
                print(" " * (width + 2) + f"{c}: {cells[c]['detail']}")
        if args.progress:
            print("@@live " + json.dumps({"id": pid, "live": live, "cells": cells}), flush=True)
    for key, error in stores.errors.items():
        print(f"\nWARNING {key} couldn't be checked: {error}")
    print(f"\n{live_count} of {len(products)} product(s) live for players. Read-only: nothing was changed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
