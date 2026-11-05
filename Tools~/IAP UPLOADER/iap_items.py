"""Reads the products in an export so they can be listed and picked before uploading.

Used by `upload_all.py --list` (the DLC Store Export window in Unity shows the result). Each item is a dict:
    key      what the upload scripts print in their summaries for this item
    arg      what to pass to --<platform>-item to upload just this item
    id       product ID, name: display name, details: type and price
    problem  None, or why the item can't be uploaded (a broken file is listed, not hidden)
"""
import json
import re
from pathlib import Path


def _read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def _money(price):
    value = int(price.get("units", 0)) + int(price.get("nanos", 0)) / 1e9
    return f"{value:.2f} {price.get('currencyCode', '')}".strip()


def _broken(name, e):
    return {"key": name, "arg": name, "id": name, "name": "", "details": "",
            "problem": f"cannot read ({type(e).__name__}: {e})"}


def load_apple(root):
    root = Path(root)
    folders = sorted(p for p in root.iterdir() if p.is_dir() and re.fullmatch(r"\d{4}", p.name)) if root.is_dir() else []
    items = []
    for folder in folders:
        try:
            create = _read(next(iter(sorted(folder.glob("*-create.json")))))
            attrs = create["data"]["attributes"]
            details = [attrs.get("inAppPurchaseType", "")]
            price_file = next(iter(sorted(folder.glob("*-pricepoint.json"))), None)
            if price_file:
                spec = _read(price_file)
                details.append(f"{spec.get('customerPrice')} ({spec.get('territory', 'USA')})")
            problem = None
            reserve = next(iter(sorted(folder.glob("*-screenshot-reserve.json"))), None)
            if reserve:
                shot_name = _read(reserve)["data"]["attributes"]["fileName"]
                if not (folder / shot_name).exists():
                    problem = f"screenshot file missing: {shot_name}"
            items.append({"key": f"{folder.name} ({attrs['productId']})", "arg": folder.name, "id": attrs["productId"],
                          "name": attrs.get("name", ""), "details": ", ".join(d for d in details if d), "problem": problem})
        except Exception as e:
            items.append(_broken(folder.name, e))
    return items


def load_google(target):
    target = Path(target)
    files = [target] if target.is_file() else (sorted(target.glob("*.json")) if target.is_dir() else [])
    items = []
    for f in files:
        try:
            data = _read(f)
            requests_ = data.get("requests") if isinstance(data, dict) else None
            if not isinstance(requests_, list):
                continue
            for req in requests_:
                prod = req["oneTimeProduct"]
                title = (prod.get("listings") or [{}])[0].get("title", "")
                prices = [f"{c.get('regionCode')} {_money(c['price'])}"
                          for o in prod.get("purchaseOptions", [])
                          for c in o.get("regionalPricingAndAvailabilityConfigs", []) if "price" in c]
                items.append({"key": prod["productId"], "arg": prod["productId"], "id": prod["productId"],
                              "name": title, "details": ", ".join(prices), "problem": None})
        except Exception as e:
            items.append(_broken(f.name, e))
    return items


def load_playfab(target):
    target = Path(target)
    files = [target] if target.is_file() else (sorted(target.rglob("*.json")) if target.is_dir() else [])
    items = []
    for f in files:
        if "credentials" in f.name.lower():
            continue
        try:
            item = _read(f)["Item"]
            fid = next(a["Value"] for a in item["AlternateIds"] if a.get("Type") == "FriendlyId")
            items.append({"key": fid, "arg": fid, "id": fid, "name": item.get("Title", {}).get("NEUTRAL", ""),
                          "details": item.get("ContentType", ""), "problem": None})
        except Exception as e:
            items.append(_broken(f.name, e))
    return items


def load_items(apple_root, google_input, playfab_input):
    return {"apple": load_apple(apple_root), "google": load_google(google_input), "playfab": load_playfab(playfab_input)}
