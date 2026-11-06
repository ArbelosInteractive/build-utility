"""Store icons for PlayFab items, uploaded as the item's Thumbnail image.

For each item the icon is <folder>/<FriendlyId>.png (a product or bundle ID), else
<folder>/default_<ContentType>.png (e.g. default_wrestler.png). An item with neither can't be uploaded.

The icon's SHA-256 is written into the item as DisplayProperties.dlcPump.thumbnailSha256 (the same field the
DLC PlayFab Review window uses). So the staging receipt's item hash covers the icon too, and an icon that is
already on the item isn't uploaded again.
No dependencies beyond the standard library, so the item listing can use it too.
"""
import hashlib
from pathlib import Path

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


class Icon:
    def __init__(self, path, data):
        self.path = Path(path)
        self.data = data
        self.sha = hashlib.sha256(data).hexdigest()
        self.is_default = self.path.name.lower().startswith("default_")


def _friendly_id(item):
    for alt in item.get("AlternateIds") or []:
        if alt.get("Type") == "FriendlyId":
            return alt.get("Value")
    return None


def find_icon(folder, friendly_id, content_type):
    """The icon file for an item, or None: <FriendlyId>.png first, then default_<ContentType>.png."""
    folder = Path(folder)
    names = [f"{friendly_id}.png"] if friendly_id else []
    if content_type:
        names.append(f"default_{content_type}.png")
    for name in names:
        if (folder / name).is_file():
            return folder / name
    return None


def attach_icon(body, folder):
    """Finds the item's icon, writes its hash into body["Item"] and returns it. Raises ValueError without an icon."""
    item = body["Item"]
    fid, content_type = _friendly_id(item), item.get("ContentType")
    path = find_icon(folder, fid, content_type)
    if path is None:
        raise ValueError(f"no icon: neither {fid}.png nor default_{content_type}.png is in {folder}")
    data = path.read_bytes()
    if not data.startswith(PNG_SIGNATURE):
        raise ValueError(f"icon {path.name} is not a PNG file")
    icon = Icon(path, data)
    display = item.get("DisplayProperties")
    if not isinstance(display, dict):
        display = item["DisplayProperties"] = {}
    pump = display.get("dlcPump")
    if not isinstance(pump, dict):
        pump = display["dlcPump"] = {}
    pump["thumbnailSha256"] = icon.sha
    return icon


def has_icon(existing, icon):
    """Whether a PlayFab draft already shows this icon: same hash recorded and a Thumbnail image with a URL."""
    pump = ((existing or {}).get("DisplayProperties") or {}).get("dlcPump") or {}
    images = (existing or {}).get("Images") or []
    return pump.get("thumbnailSha256") == icon.sha and any(
        i.get("Type") == "Thumbnail" and i.get("Url") for i in images if isinstance(i, dict))
