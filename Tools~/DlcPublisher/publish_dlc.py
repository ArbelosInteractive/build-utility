#!/usr/bin/env python3
r"""Publish DLC product manifests and the Addressables remote catalog to Azure Blob Storage.

Layout in the container (default "dlc-content"):

    <Platform>/<appVersion>/catalog_<x>.bin + .hash   one remote catalog per app version
    <Platform>/<appVersion>/<bundles...>              content-hash names, never overwritten
    <Platform>/<appVersion>/products.json             mutable index, the only file the game reads first
    <Platform>/<appVersion>/<productId>/v<N>/product.json   immutable
    _build-state/<Platform>/<appVersion>/addressables_content_state.bin   the full build's content-state file,
                                                      for content updates; not used by the game

Commands:
    publish   upload an export made by Build Utility > DLC Content > Export Selected Releases
    rollback  rewrite products.json to point a product at an earlier version, or drop a version
    status    print products.json for a platform and app version
    check     sign in and confirm the container can be read (writes nothing)
    baseline  download the stored content-state file for a platform and app version

Publish order (always): bundles -> catalog, then its .hash -> v<N>/product.json -> products.json.
  * A bundle that already exists with the same MD5 is skipped; a different MD5 under the same name fails.
  * v<N> is the next free number for that product in that app version; an existing v<N> fails the run.
  * products.json is written with If-Match on its ETag (or If-None-Match: * when new), so two publishers
    can't overwrite each other's changes.
  * If the app-version folder already has a catalog with a different name (a full rebuild of a published
    version), the run fails before uploading anything. Use a content update, or --allow-new-catalog for a
    version that hasn't shipped.
  * A failed upload is retried a few times. A bundle that still fails doesn't stop the other bundles, but the
    run stops before the catalog. Any other error stops the run straight away. Nothing is ever deleted.

Content-state baseline: when export.json names the full build's addressables_content_state.bin (the exporter
only does that when the file belongs to the catalog being published), it is stored once per platform and app
version during step 2. The same file again is skipped; a different file fails the run before anything is written,
unless --allow-new-catalog is given (a version that hasn't shipped). Content updates never change it: every update
of a version is built against the file from the full build players have.

Resuming: run the same publish again. Bundles and catalog files already uploaded are skipped (same MD5), and a
v<N>/product.json left by the interrupted run (written but not yet listed in products.json) is reused when it
holds the same draft, so products don't skip version numbers.

--progress prints "@@progress {json}" lines on stdout for the Unity window (not written to the log).

Sign-in: the current Azure CLI user (`az login`), who needs "Storage Blob Data Contributor" on the account.
Install: pip install azure-identity azure-storage-blob
Usage:   python publish_dlc.py publish --staging <export dir> --account-url https://<acct>.blob.core.windows.net --dry-run
         python publish_dlc.py publish --staging <export dir> --account-url ... --product wrestler_jazz
         python publish_dlc.py rollback --account-url ... --platform Android --app-version 1.4.0 --product wrestler_jazz --to v1
         python publish_dlc.py rollback ... --product wrestler_jazz --drop v2
"""
import argparse
import datetime
import hashlib
import json
import re
import sys
import time
from pathlib import Path

SCHEMA_VERSION = 1
SEGMENT = re.compile(r"^[A-Za-z0-9._-]+$")
VERSION_FOLDER = re.compile(r"^v([1-9][0-9]*)$")
CATALOG_FILE = re.compile(r"^catalog.*\.(bin|json|hash)$")
IMMUTABLE = "public, max-age=31536000, immutable"
SHORT = "public, max-age=60"
CONTENT_TYPES = {".json": "application/json", ".hash": "text/plain", ".bin": "application/octet-stream",
                 ".bundle": "application/octet-stream"}
BASELINE_DIR = "_build-state"
BASELINE_FILE = "addressables_content_state.bin"
RETRY_DELAYS = (3, 10)  # seconds before the 2nd and 3rd attempt of a failed upload; tests set these to 0
PUBLISH_STEPS = 4


class PublishError(Exception):
    pass


def safe_segment(value, what):
    if not isinstance(value, str) or value in (".", "..") or not SEGMENT.match(value):
        raise PublishError(f"{what} '{value}' can only contain letters, digits, '.', '_' and '-'.")
    return value


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def md5_bytes(data):
    return hashlib.md5(data).digest()


def md5_file(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.digest()


def dumps(obj):
    return (json.dumps(obj, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


# --------------------------------------------------------------------------------------- storage

class Conflict(Exception):
    """A create-only or If-Match write lost against another writer."""


class AzureStore:
    """The few blob operations the publisher needs. Tests swap in a fake with the same methods."""

    def __init__(self, account_url, container):
        try:
            from azure.identity import AzureCliCredential
            from azure.storage.blob import BlobServiceClient
        except ImportError:
            raise PublishError("Missing packages. Run: pip install azure-identity azure-storage-blob")
        service = BlobServiceClient(account_url, credential=AzureCliCredential())
        self.container = service.get_container_client(container)
        try:
            if not self.container.exists():
                raise PublishError(f"Container '{container}' doesn't exist at {account_url}. Create it first (see README).")
        except PublishError:
            raise
        except Exception as e:
            raise PublishError(f"Can't reach {account_url}/{container}: {e}. Sign in with `az login` and check the "
                               "Storage Blob Data Contributor role.")

    def read(self, name):
        """(bytes, etag), or None when the blob doesn't exist."""
        from azure.core.exceptions import ResourceNotFoundError
        try:
            downloader = self.container.get_blob_client(name).download_blob()
            return downloader.readall(), downloader.properties.etag
        except ResourceNotFoundError:
            return None

    def md5(self, name):
        """Stored Content-MD5, b"" if the blob has none, or None when the blob doesn't exist."""
        from azure.core.exceptions import ResourceNotFoundError
        try:
            props = self.container.get_blob_client(name).get_blob_properties()
        except ResourceNotFoundError:
            return None
        value = props.content_settings.content_md5
        return bytes(value) if value else b""

    def list(self, prefix):
        return [b.name for b in self.container.list_blobs(name_starts_with=prefix)]

    def write(self, name, data, content_type, cache_control, create_only=False, etag=None, progress=None):
        """progress: optional callable(bytes_sent) while the upload runs."""
        from azure.core import MatchConditions
        from azure.core.exceptions import ResourceExistsError, ResourceModifiedError
        from azure.storage.blob import ContentSettings
        settings = ContentSettings(content_type=content_type, cache_control=cache_control,
                                   content_md5=bytearray(md5_bytes(data) if isinstance(data, bytes) else data[1]))
        body = data if isinstance(data, bytes) else open(data[0], "rb")
        extra = {"progress_hook": lambda current, total: progress(current)} if progress else {}
        try:
            client = self.container.get_blob_client(name)
            if etag:
                client.upload_blob(body, overwrite=True, content_settings=settings, etag=etag,
                                   match_condition=MatchConditions.IfNotModified, **extra)
            else:
                client.upload_blob(body, overwrite=not create_only, content_settings=settings, **extra)
        except (ResourceExistsError, ResourceModifiedError) as e:
            raise Conflict(str(e).splitlines()[0])
        finally:
            if not isinstance(data, bytes):
                body.close()


# --------------------------------------------------------------------------------------- run log

class Run:
    def __init__(self, log_path, dry_run, progress=False):
        self.dry_run = dry_run
        self.created, self.skipped, self.warnings = [], [], []
        log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path = log_path
        self.log = open(log_path, "a", encoding="utf-8")
        self.progress_enabled = progress
        self._last_progress = 0.0

    def progress(self, step, phase, done, total, unit, item="", files_done=0, files_total=0, force=False):
        """Machine-readable progress for the Unity window; stdout only, at most ~5 per second unless forced."""
        if not self.progress_enabled:
            return
        now = time.monotonic()
        if not force and now - self._last_progress < 0.2:
            return
        self._last_progress = now
        print("@@progress " + json.dumps({"step": step, "steps": PUBLISH_STEPS, "phase": phase, "done": done,
                                          "total": total, "unit": unit, "item": item, "filesDone": files_done,
                                          "filesTotal": files_total}), flush=True)

    def say(self, text=""):
        print(text, flush=True)
        self.log.write(text + "\n")
        self.log.flush()

    def did(self, what):
        self.created.append(what)
        self.say(("WOULD CREATE " if self.dry_run else "created  ") + what)

    def skip(self, what):
        self.skipped.append(what)
        self.say("skipped  " + what)

    def warn(self, what):
        self.warnings.append(what)
        self.say("WARNING  " + what)

    def summary(self, ok, error=None):
        self.say("")
        self.say(("DRY RUN " if self.dry_run else "") + ("SUCCEEDED" if ok else "FAILED"))
        if error:
            self.say("error: " + error)
        self.say(f"{'would create' if self.dry_run else 'created'}: {len(self.created)}, skipped: {len(self.skipped)}, "
                 f"warnings: {len(self.warnings)}")
        for w in self.warnings:
            self.say("  warning: " + w)
        self.say("log: " + str(self.log_path))
        self.log.close()


# --------------------------------------------------------------------------------------- index helpers

def load_index(store, prefix, platform, app_version):
    """(index dict, etag or None)."""
    found = store.read(prefix + "products.json")
    if found is None:
        return {"schemaVersion": SCHEMA_VERSION, "platform": platform, "appVersion": app_version,
                "catalogBuild": "", "updatedUtc": "", "products": []}, None
    data, etag = found
    try:
        index = json.loads(data.decode("utf-8"))
    except ValueError as e:
        raise PublishError(f"Remote {prefix}products.json isn't valid JSON: {e}")
    if index.get("schemaVersion") != SCHEMA_VERSION or index.get("platform") != platform or index.get("appVersion") != app_version:
        raise PublishError(f"Remote {prefix}products.json has unexpected schemaVersion/platform/appVersion.")
    if not isinstance(index.get("products"), list):
        raise PublishError(f"Remote {prefix}products.json has no products list.")
    return index, etag


def find_entry(index, product_id):
    return next((p for p in index["products"] if p.get("productId") == product_id), None)


def save_index(store, run, prefix, index, etag):
    index["updatedUtc"] = utc_now()
    index["products"].sort(key=lambda p: p["productId"])
    name = prefix + "products.json"
    if run.dry_run:
        run.did(name + (" (update, If-Match " + etag + ")" if etag else " (new)"))
        return
    try:
        store.write(name, dumps(index), "application/json", SHORT, create_only=etag is None, etag=etag)
    except Conflict:
        raise PublishError(f"{name} was changed by someone else during this run. Nothing in it was overwritten; "
                           "run the command again to pick up their changes.")
    run.did(name)


def version_numbers(store, prefix, product_id, entry):
    numbers = set()
    for name in store.list(f"{prefix}{product_id}/v"):
        m = VERSION_FOLDER.match(name[len(prefix) + len(product_id) + 1:].split("/", 1)[0])
        if m:
            numbers.add(int(m.group(1)))
    for v in (entry or {}).get("versions", []):
        m = VERSION_FOLDER.match(v)
        if m:
            numbers.add(int(m.group(1)))
    return numbers


# --------------------------------------------------------------------------------------- publish

def read_export(staging):
    staging = Path(staging).resolve()
    path = staging / "export.json"
    if not path.is_file():
        raise PublishError(f"{path} not found. Pass the folder written by 'Export Selected Releases'.")
    export = json.loads(path.read_text(encoding="utf-8"))
    if export.get("schemaVersion") != SCHEMA_VERSION or export.get("kind") != "dlc-content-export":
        raise PublishError(f"{path} isn't a DLC content export (schemaVersion {SCHEMA_VERSION}).")
    for field in ("platform", "appVersion", "catalogFile", "hashFile", "catalogBuild", "addressablesBuildPath", "products"):
        if not export.get(field):
            raise PublishError(f"export.json is missing '{field}'.")
    safe_segment(export["platform"], "Platform")
    safe_segment(export["appVersion"], "App version")
    return staging, export


def check_build(export):
    build = Path(export["addressablesBuildPath"])
    if not build.is_dir():
        raise PublishError(f"Addressables build folder {build} doesn't exist.")
    catalog, hash_file = build / export["catalogFile"], build / export["hashFile"]
    if not catalog.is_file() or not hash_file.is_file():
        raise PublishError(f"Catalog {catalog.name} or {hash_file.name} is missing from {build}.")
    if hash_file.read_text(encoding="utf-8").strip() != export["catalogBuild"]:
        raise PublishError("The Addressables build changed after the export (catalog hash differs). Export again.")
    bundles = []
    for path in sorted(p for p in build.rglob("*") if p.is_file()):
        rel = path.relative_to(build).as_posix()
        if any(part in (".", "..") or not SEGMENT.match(part) for part in rel.split("/")):
            raise PublishError(f"Unsupported file name in build output: {rel}")
        if CATALOG_FILE.match(path.name) and "/" not in rel:
            continue  # catalogs are handled separately; older ones in the folder are not published
        bundles.append((rel, path))
    return build, catalog, hash_file, bundles


def load_products(staging, export, only):
    wanted = list(export["products"])
    if only:
        unknown = [p for p in only if p not in wanted]
        if unknown:
            raise PublishError("Not in this export: " + ", ".join(unknown))
        wanted = [p for p in wanted if p in only]
    products = []
    for pid in wanted:
        safe_segment(pid, "Product ID")
        path = staging / pid / "product.json"
        if not path.is_file():
            raise PublishError(f"{path} is missing.")
        doc = json.loads(path.read_text(encoding="utf-8"))
        if doc.get("productId") != pid or doc.get("schemaVersion") != SCHEMA_VERSION:
            raise PublishError(f"{path} has the wrong productId or schemaVersion.")
        if doc.get("platform") != export["platform"] or doc.get("appVersion") != export["appVersion"]:
            raise PublishError(f"{path} is for a different platform or app version than export.json.")
        if doc.get("catalogBuild") != export["catalogBuild"]:
            raise PublishError(f"{path} was built against a different catalog than export.json.")
        if doc.get("version") is not None:
            raise PublishError(f"{path} already has a version; staging files are drafts. Export again.")
        keys = doc.get("keys")
        # An empty list is allowed: the product belongs to this app version but its content ships in the app.
        if not isinstance(keys, list) or not all(isinstance(k, str) and k for k in keys):
            raise PublishError(f"{path} has an invalid keys list.")
        products.append(doc)
    return products


def with_retries(run, what, action):
    """Runs action(); retries transient failures (anything but Conflict/PublishError) after RETRY_DELAYS."""
    for attempt in range(len(RETRY_DELAYS) + 1):
        try:
            return action()
        except (Conflict, PublishError):
            raise
        except Exception as e:
            if attempt == len(RETRY_DELAYS):
                raise PublishError(f"{what} failed after {attempt + 1} attempts: {type(e).__name__}: {e}")
            run.say(f"retrying {what} in {RETRY_DELAYS[attempt]}s ({type(e).__name__}: {str(e).splitlines()[0] if str(e) else ''})")
            time.sleep(RETRY_DELAYS[attempt])


def upload_file(store, run, name, path, cache_control, create_only, on_bytes=None):
    """on_bytes: optional callable(bytes_sent_of_this_file) for progress."""
    local = md5_file(path)
    remote = with_retries(run, "checking " + name, lambda: store.md5(name))
    if remote is not None:
        if remote == local:
            run.skip(name + " (identical)")
            return
        if create_only:
            raise PublishError(f"{name} already exists with different content. Bundles are never overwritten; "
                               "check that the build is deterministic and the Remote.BuildPath is for this app version.")
    if run.dry_run:
        run.did(name)
        return
    try:
        with_retries(run, "uploading " + name, lambda: store.write(
            name, (path, local), CONTENT_TYPES.get(path.suffix, "application/octet-stream"), cache_control,
            create_only=create_only, progress=on_bytes))
    except Conflict:
        # A retry after a create-only upload that actually landed: same MD5 means it's ours.
        if create_only and store.md5(name) == local:
            run.skip(name + " (identical, landed on an earlier attempt)")
            return
        raise PublishError(f"{name} was created by someone else during this run.")
    run.did(name)


def upload_files(store, run, step, phase, files, cache_control, create_only, keep_going):
    """Uploads (name, path) pairs with byte progress. keep_going: a failed file doesn't stop the others; the
    failures are raised together at the end, before the caller moves on to the next step."""
    sizes = [path.stat().st_size for _, path in files]
    total, done, failures = sum(sizes), 0, []
    run.progress(step, phase, 0, total, "bytes", "", 0, len(files), force=True)
    for i, ((name, path), size) in enumerate(zip(files, sizes)):
        base = done
        try:
            upload_file(store, run, name, path, cache_control, create_only,
                        on_bytes=lambda sent, base=base, i=i, name=name: run.progress(
                            step, phase, base + min(sent, size), total, "bytes", name, i, len(files)))
        except PublishError as e:
            if not keep_going:
                raise
            failures.append(str(e))
            run.say("FAILED   " + str(e))
        done += size
        run.progress(step, phase, done, total, "bytes", name, i + 1, len(files), force=i + 1 == len(files))
    if failures:
        raise PublishError(f"{len(failures)} of {len(files)} file(s) in '{phase}' failed; nothing after them was "
                           "written. Run the same publish again to upload only what's missing.\n  "
                           + "\n  ".join(failures[:20]) + ("\n  ..." if len(failures) > 20 else ""))


def strip_published(doc):
    return {k: v for k, v in doc.items() if k not in ("version", "publishedUtc")}


def interrupted_version(store, prefix, doc, entry, numbers):
    """An interrupted publish can leave v<N>/product.json written but not yet listed in products.json. If the newest
    such folder holds this same draft, return N so the rerun reuses it instead of taking the next number."""
    if not numbers:
        return None
    top = max(numbers)
    if f"v{top}" in (entry or {}).get("versions", []):
        return None
    found = store.read(f"{prefix}{doc['productId']}/v{top}/product.json")
    if found is None:
        return None
    try:
        remote = json.loads(found[0].decode("utf-8"))
    except ValueError:
        return None
    return top if isinstance(remote, dict) and strip_published(remote) == strip_published(doc) else None


def baseline_name(platform, app_version):
    return f"{BASELINE_DIR}/{platform}/{app_version}/{BASELINE_FILE}"


def plan_baseline(store, run, export, platform, app_version, allow_new_catalog):
    """(blob name, local path, overwrite) to upload in step 2, or None. Checked before anything is written."""
    name = baseline_name(platform, app_version)
    local = export.get("contentStateFile")
    if not local:
        if store.md5(name) is None:
            run.warn(f"No content-state baseline is stored for {platform} {app_version} and this export has none "
                     "(see the exporter's warning). Content updates of this version need the .bin from its full build.")
        return None
    path = Path(local)
    if not path.is_file():
        raise PublishError(f"Content-state file {path} named in export.json is missing. Export again.")
    remote = store.md5(name)
    if remote is None or remote == md5_file(path):
        return name, path, False
    message = (f"{name} already holds a different content-state file. It must stay the one from the full build "
               "players have: every content update of this version is built against it.")
    if not allow_new_catalog:
        raise PublishError(message + " This export comes from another full build; use a content update instead. "
                           "If this app version hasn't shipped to players yet, pass --allow-new-catalog.")
    run.warn(message + " Replacing it because --allow-new-catalog was given.")
    return name, path, True


def check_catalog_name(store, run, prefix, catalog_name, allow_new_catalog):
    """Installed players of an app version look for the catalog name baked into their build. A full rebuild of
    that version makes a catalog with a new name, which those players never load. Content updates keep the name."""
    remote = remote_catalogs(store, prefix)
    if not remote or catalog_name in remote:
        return
    message = (f"{prefix} already has catalog {', '.join(remote)}, but this build produced {catalog_name}. "
               "Players of this app version only load the catalog their build points at, so this looks like a full "
               "rebuild of a version that already has a published catalog. Use a content update (Update a Previous "
               "Build with this version's content-state file) to keep the catalog name.")
    if not allow_new_catalog:
        raise PublishError(message + " If this app version hasn't shipped to players yet, pass --allow-new-catalog.")
    run.warn(message + " Publishing anyway because --allow-new-catalog was given; the old catalog is left in place.")


def publish(store, run, staging, only, allow_new_catalog=False):
    staging, export = read_export(staging)
    platform, app_version = export["platform"], export["appVersion"]
    prefix = f"{platform}/{app_version}/"
    run.say(f"Publishing {staging}\n  -> {prefix} (catalog {export['catalogFile']}, build {export['catalogBuild']})")

    build, catalog, hash_file, bundles = check_build(export)
    check_catalog_name(store, run, prefix, catalog.name, allow_new_catalog)
    baseline = plan_baseline(store, run, export, platform, app_version, allow_new_catalog)
    products = load_products(staging, export, only)
    index, etag = load_index(store, prefix, platform, app_version)

    # Preflight: assign versions and compare keys with the live version, before anything is written.
    plan = []
    for doc in products:
        pid = doc["productId"]
        entry = find_entry(index, pid)
        numbers = version_numbers(store, prefix, pid, entry)
        resumed = interrupted_version(store, prefix, doc, entry, numbers)
        number = resumed or max(numbers, default=0) + 1
        if entry and entry.get("current"):
            live = store.read(f"{prefix}{pid}/{entry['current']}/product.json")
            if live is None:
                run.warn(f"{pid}: products.json points at {entry['current']}, but its product.json is missing.")
            else:
                old_keys = set(json.loads(live[0].decode("utf-8")).get("keys", []))
                removed = sorted(old_keys - set(doc["keys"]))
                if removed:
                    run.warn(f"{pid}: {len(removed)} key(s) published in {entry['current']} are gone in v{number} "
                             f"(keys must only be added): " + ", ".join(removed[:10]) + (" ..." if len(removed) > 10 else ""))
        plan.append((doc, entry, number, resumed is not None))
        run.say(f"  {pid}: v{number} ({len(doc['keys'])} keys{', local content only' if not doc['keys'] else ''})"
                + (f", replaces {entry['current']}" if entry and entry.get("current") else ", new")
                + (", resuming an interrupted publish" if resumed is not None else ""))

    run.say("\n1. Bundles")
    upload_files(store, run, 1, "Bundles", [(prefix + rel, path) for rel, path in bundles], IMMUTABLE,
                 create_only=True, keep_going=True)

    run.say("\n2. Catalog")
    upload_files(store, run, 2, "Catalog", [(prefix + catalog.name, catalog), (prefix + hash_file.name, hash_file)],
                 SHORT, create_only=False, keep_going=False)
    if baseline:
        name, path, overwrite = baseline
        upload_files(store, run, 2, "Catalog", [(name, path)], "no-store", create_only=not overwrite, keep_going=False)

    run.say("\n3. Product manifests")
    for i, (doc, entry, number, resumed) in enumerate(plan):
        name = f"{prefix}{doc['productId']}/v{number}/product.json"
        run.progress(3, "Product manifests", i, len(plan), "files", name, i, len(plan))
        if resumed:
            run.skip(name + " (written by the interrupted publish, same content)")
            continue
        if run.dry_run:
            run.did(name)
            continue
        final = dict(doc, version=number, publishedUtc=utc_now())
        try:
            with_retries(run, "uploading " + name, lambda: store.write(
                name, dumps(final), "application/json", IMMUTABLE, create_only=True))
        except Conflict:
            # A retry after an upload that actually landed shows up as a conflict: check whether it's ours.
            found = store.read(name)
            mine = found is not None and strip_published(json.loads(found[0].decode("utf-8"))) == strip_published(final)
            if not mine:
                raise PublishError(f"{name} already exists. Version folders are never overwritten; run again to take the next number.")
        run.did(name)
    run.progress(3, "Product manifests", len(plan), len(plan), "files", "", len(plan), len(plan), force=True)

    run.say("\n4. products.json")
    run.progress(4, "products.json", 0, 1, "files", prefix + "products.json", 0, 1, force=True)
    for doc, entry, number, _ in plan:
        version = f"v{number}"
        if entry is None:
            entry = {"productId": doc["productId"], "current": version, "versions": []}
            index["products"].append(entry)
        entry["current"] = version
        entry["versions"] = [version] + [v for v in entry.get("versions", []) if v != version]
    index["catalogBuild"] = export["catalogBuild"]
    save_index(store, run, prefix, index, etag)
    run.progress(4, "products.json", 1, 1, "files", prefix + "products.json", 1, 1, force=True)


# --------------------------------------------------------------------------------------- rollback / status

def rollback(store, run, platform, app_version, product_id, to=None, drop=None):
    for value, what in ((platform, "Platform"), (app_version, "App version"), (product_id, "Product ID")):
        safe_segment(value, what)
    prefix = f"{platform}/{app_version}/"
    index, etag = load_index(store, prefix, platform, app_version)
    if etag is None:
        raise PublishError(f"No {prefix}products.json exists.")
    entry = find_entry(index, product_id)
    if entry is None:
        raise PublishError(f"{product_id} isn't in {prefix}products.json.")
    versions = list(entry.get("versions", []))
    target = to or drop
    if not VERSION_FOLDER.match(target or ""):
        raise PublishError(f"'{target}' isn't a version folder like v2.")
    if target not in versions:
        raise PublishError(f"{product_id} has no {target} in {prefix}products.json (listed: {', '.join(versions)}).")

    if to:
        if store.read(f"{prefix}{product_id}/{to}/product.json") is None:
            raise PublishError(f"{prefix}{product_id}/{to}/product.json is missing; can't point at it.")
        run.say(f"{product_id}: current {entry['current']} -> {to}")
        entry["current"] = to
        entry["versions"] = [to] + [v for v in versions if v != to]
    else:
        remaining = [v for v in versions if v != drop]
        if not remaining:
            raise PublishError(f"{drop} is the only version of {product_id}; it can't be dropped.")
        run.say(f"{product_id}: dropping {drop} from the list (its folder is kept)")
        entry["versions"] = remaining
        if entry["current"] == drop:
            entry["current"] = remaining[0]
            run.say(f"{product_id}: current -> {remaining[0]}")
    save_index(store, run, prefix, index, etag)


def download_baseline(store, run, platform, app_version, out):
    name = baseline_name(safe_segment(platform, "Platform"), safe_segment(app_version, "App version"))
    found = store.read(name)
    if found is None:
        raise PublishError(f"No content-state baseline is stored at {name}. It's uploaded when a full build of "
                           f"{platform} {app_version} is published; without it, pick the .bin from that build by hand.")
    out = Path(out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    tmp = out.with_name(out.name + ".download")
    tmp.write_bytes(found[0])
    tmp.replace(out)
    run.say(f"Downloaded {name} ({len(found[0])} bytes) to {out}")


def remote_catalogs(store, prefix):
    return sorted({rel for rel in (name[len(prefix):] for name in store.list(prefix + "catalog"))
                   if "/" not in rel and re.match(r"^catalog.*\.(bin|json)$", rel)})


def status(store, run, platform, app_version, state_json=False):
    prefix = f"{safe_segment(platform, 'Platform')}/{safe_segment(app_version, 'App version')}/"
    index, etag = load_index(store, prefix, platform, app_version)
    if state_json:
        # One machine-readable line for the Unity window's build suggestion.
        print("@@state " + json.dumps({"catalogs": remote_catalogs(store, prefix),
                                       "baseline": store.md5(baseline_name(platform, app_version)) is not None,
                                       "products": len(index["products"])}), flush=True)
    if etag is None:
        run.say(f"No {prefix}products.json yet.")
        return
    run.say(f"{prefix}products.json (ETag {etag}, catalog {index.get('catalogBuild')}, updated {index.get('updatedUtc')})")
    for p in sorted(index["products"], key=lambda p: p["productId"]):
        run.say(f"  {p['productId']}: current {p.get('current')}  versions {', '.join(p.get('versions', []))}")


# --------------------------------------------------------------------------------------- main

def resume_hint(run, args):
    if args.command == "publish" and not args.dry_run and run.created:
        run.say("\nTo continue: run the same publish again. Files already uploaded are skipped and manifests written "
                "by this run are reused; products.json is only written at the very end.")


def main(argv=None, store_factory=AzureStore):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--account-url", required=True, help="https://<account>.blob.core.windows.net")
        p.add_argument("--container", default="dlc-content")
        p.add_argument("--dry-run", action="store_true", help="read and report, write nothing")
        p.add_argument("--log-dir", type=Path, default=Path("Logs") / "dlc-publish")
        p.add_argument("--log-file", type=Path, help="exact log file instead of a timestamped one in --log-dir")
        p.add_argument("--progress", action="store_true", help="print @@progress JSON lines (for the Unity window)")

    p = sub.add_parser("publish", help="upload an export")
    common(p)
    p.add_argument("--staging", required=True, type=Path, help="export folder containing export.json")
    p.add_argument("--product", nargs="+", metavar="ID", help="publish only these products from the export")
    p.add_argument("--allow-new-catalog", action="store_true",
                   help="publish even if the app-version folder already has a catalog with a different name "
                        "(only for a version that hasn't shipped)")

    p = sub.add_parser("rollback", help="point a product at an earlier version, or drop one")
    common(p)
    p.add_argument("--platform", required=True)
    p.add_argument("--app-version", required=True)
    p.add_argument("--product", required=True)
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--to", metavar="vN", help="make this version current")
    g.add_argument("--drop", metavar="vN", help="remove this version from the list; its folder stays")

    p = sub.add_parser("check", help="confirm the signed-in user can reach the container")
    common(p)

    p = sub.add_parser("baseline", help="download the stored content-state file")
    common(p)
    p.add_argument("--platform", required=True)
    p.add_argument("--app-version", required=True)
    p.add_argument("--out", required=True, type=Path, help="where to save addressables_content_state.bin")

    p = sub.add_parser("status", help="print products.json")
    common(p)
    p.add_argument("--platform", required=True)
    p.add_argument("--app-version", required=True)
    p.add_argument("--state-json", action="store_true", help="also print an @@state JSON line (for the Unity window)")

    args = ap.parse_args(argv)
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    run = Run(args.log_file or args.log_dir / f"{args.command}_{stamp}.log", args.dry_run, args.progress)
    run.say(f"{args.command} {'(dry run) ' if args.dry_run else ''}{args.account_url}/{args.container}  {utc_now()}")
    try:
        if not re.match(r"^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", args.container):
            raise PublishError(f"Invalid container name '{args.container}'.")
        store = store_factory(args.account_url, args.container)
        if args.command == "publish":
            publish(store, run, args.staging, args.product, args.allow_new_catalog)
        elif args.command == "check":
            run.say(f"OK: signed in and can read {args.account_url}/{args.container}.")
        elif args.command == "baseline":
            download_baseline(store, run, args.platform, args.app_version, args.out)
        elif args.command == "rollback":
            rollback(store, run, args.platform, args.app_version, args.product, to=args.to, drop=args.drop)
        else:
            status(store, run, args.platform, args.app_version, args.state_json)
    except PublishError as e:
        resume_hint(run, args)
        run.summary(False, str(e))
        return 1
    except Exception as e:  # unexpected: still stop, still summarize
        resume_hint(run, args)
        run.summary(False, f"{type(e).__name__}: {e}")
        return 1
    run.summary(True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
