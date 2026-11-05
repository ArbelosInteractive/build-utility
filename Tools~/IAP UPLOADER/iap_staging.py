"""The PlayFab staging receipt: which items went to the staging title, as which file.

A staging upload records every item it sends in playfab-staging-receipt.json in the export root (the folder
holding the playfab folder), with a hash of the item. A production upload checks that every item it sends is in
the receipt with the same hash, i.e. exactly this file was uploaded to staging first.
No dependencies beyond the standard library, so the item listing can use it too.
"""
import datetime
import hashlib
import json

RECEIPT_NAME = "playfab-staging-receipt.json"


def receipt_path(input_path):
    """playfab-staging-receipt.json in the export root (the folder holding the playfab folder)."""
    folder = input_path if input_path.is_dir() else input_path.parent
    return folder.resolve().parent / RECEIPT_NAME


def item_hash(body):
    """What the receipt compares: the file's Item, independent of key order and of the Publish flag."""
    return hashlib.sha256(json.dumps(body.get("Item"), sort_keys=True, separators=(",", ":"),
                                     ensure_ascii=False).encode("utf-8")).hexdigest()


def read_receipt(path):
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"items": {}}
    return data if isinstance(data.get("items"), dict) else {"items": {}}


def record_staging(path, title_id, fid, body, published):
    """Adds one item to the receipt right after it reached staging (so a run that stops half way keeps its items)."""
    data = read_receipt(path)
    data["kind"] = "playfab-staging-receipt"
    data["stagingTitleId"] = title_id
    data["items"][fid] = {"sha256": item_hash(body), "published": bool(published),
                          "uploadedUtc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, indent=2, sort_keys=True), encoding="utf-8")
    tmp.replace(path)


def untested(path, items):
    """[(FriendlyId, reason)] for the items a production upload would send that staging hasn't seen as they are now."""
    seen = read_receipt(path)["items"]
    out = []
    for _, fid, body in items:
        entry = seen.get(fid)
        if not entry:
            out.append((fid, "never uploaded to staging"))
        elif entry.get("sha256") != item_hash(body):
            out.append((fid, f"changed since its staging upload ({entry.get('uploadedUtc', '?')})"))
    return out


def staged_state(path, fid, body):
    """'same' (uploaded to staging as it is now), 'changed' (uploaded, then the file changed) or 'no'."""
    entry = read_receipt(path)["items"].get(fid)
    return "no" if not entry else "same" if entry.get("sha256") == item_hash(body) else "changed"
