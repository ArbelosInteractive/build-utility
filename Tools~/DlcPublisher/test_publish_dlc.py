"""Tests for publish_dlc.py against an in-memory blob store. Run: python -m unittest test_publish_dlc"""
import hashlib
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import publish_dlc as p


class FakeStore:
    def __init__(self):
        self.blobs = {}  # name -> (bytes, etag)
        self.writes = []
        self.counter = 0
        self.before_write = None

    def read(self, name):
        return self.blobs.get(name)

    def md5(self, name):
        return hashlib.md5(self.blobs[name][0]).digest() if name in self.blobs else None

    def list(self, prefix):
        return sorted(n for n in self.blobs if n.startswith(prefix))

    def write(self, name, data, content_type, cache_control, create_only=False, etag=None, progress=None):
        if self.before_write:
            self.before_write(name)
        body = data if isinstance(data, bytes) else Path(data[0]).read_bytes()
        if progress:
            progress(len(body))
        if create_only and name in self.blobs:
            raise p.Conflict("exists")
        if etag and (name not in self.blobs or self.blobs[name][1] != etag):
            raise p.Conflict("etag")
        self.counter += 1
        self.blobs[name] = (body, f"etag{self.counter}")
        self.writes.append(name)


class PublishTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        p.RETRY_DELAYS = (0, 0)

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.build = self.tmp / "ServerData" / "Android" / "1.4.0"
        self.build.mkdir(parents=True)
        (self.build / "catalog_1.bin").write_bytes(b"catalog")
        (self.build / "catalog_1.hash").write_text("abc123")
        (self.build / "skins_assets_all_ff00.bundle").write_bytes(b"bundle-a")
        self.store = FakeStore()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def export(self, products, content_state=None):
        stage = self.tmp / f"stage{len(list(self.tmp.glob('stage*')))}"
        stage.mkdir()
        export = {
            "schemaVersion": 1, "kind": "dlc-content-export", "platform": "Android", "appVersion": "1.4.0",
            "catalogFile": "catalog_1.bin", "hashFile": "catalog_1.hash", "catalogBuild": "abc123",
            "addressablesBuildPath": str(self.build), "products": list(products)}
        if content_state is not None:
            state = self.tmp / f"state{len(list(self.tmp.glob('state*')))}.bin"
            state.write_bytes(content_state)
            export["contentStateFile"] = str(state)
        (stage / "export.json").write_text(json.dumps(export))
        for pid, keys in products.items():
            (stage / pid).mkdir()
            (stage / pid / "product.json").write_text(json.dumps({
                "schemaVersion": 1, "productId": pid, "version": None, "platform": "Android", "appVersion": "1.4.0",
                "releaseId": "rel", "target": "Android", "minimumClientVersion": "", "keys": keys,
                "catalogBuild": "abc123", "notes": ""}))
        return stage

    def run_cmd(self, *args):
        return p.main(["--help"] if not args else list(args) + ["--account-url", "https://x.blob.core.windows.net",
                                                                "--log-dir", str(self.tmp / "logs")],
                      store_factory=lambda url, container: self.store)

    def index(self):
        return json.loads(self.store.blobs["Android/1.4.0/products.json"][0])

    def test_publish_order_and_versions(self):
        stage = self.export({"wrestler_jazz": ["Jazz/Main"], "wrestler_x": ["X/Main"]})
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage)))
        w = self.store.writes
        self.assertEqual("Android/1.4.0/skins_assets_all_ff00.bundle", w[0])
        self.assertLess(w.index("Android/1.4.0/catalog_1.bin"), w.index("Android/1.4.0/catalog_1.hash"))
        self.assertLess(w.index("Android/1.4.0/catalog_1.hash"), w.index("Android/1.4.0/wrestler_jazz/v1/product.json"))
        self.assertEqual("Android/1.4.0/products.json", w[-1])
        idx = self.index()
        self.assertEqual({"wrestler_jazz", "wrestler_x"}, {e["productId"] for e in idx["products"]})
        manifest = json.loads(self.store.blobs["Android/1.4.0/wrestler_jazz/v1/product.json"][0])
        self.assertEqual(1, manifest["version"])

        # Second publish of one product: bundle skipped, v2 created, other product untouched.
        stage2 = self.export({"wrestler_jazz": ["Jazz/Main", "Jazz/Alt"], "wrestler_x": ["X/Main"]})
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage2), "--product", "wrestler_jazz"))
        idx = self.index()
        jazz = next(e for e in idx["products"] if e["productId"] == "wrestler_jazz")
        self.assertEqual("v2", jazz["current"])
        self.assertEqual(["v2", "v1"], jazz["versions"])
        self.assertNotIn("Android/1.4.0/wrestler_x/v2/product.json", self.store.blobs)

    def test_dry_run_writes_nothing(self):
        stage = self.export({"wrestler_jazz": ["Jazz/Main"]})
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage), "--dry-run"))
        self.assertEqual([], self.store.writes)

    def test_changed_bundle_with_same_name_fails_before_catalog(self):
        self.store.blobs["Android/1.4.0/skins_assets_all_ff00.bundle"] = (b"other", "e0")
        stage = self.export({"wrestler_jazz": ["Jazz/Main"]})
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertNotIn("Android/1.4.0/catalog_1.bin", self.store.blobs)
        self.assertNotIn("Android/1.4.0/products.json", self.store.blobs)

    def test_existing_version_folder_is_never_overwritten(self):
        stage = self.export({"wrestler_jazz": ["Jazz/Main"]})
        # Someone else creates v1 between preflight and upload.
        def race(name):
            if name.endswith("catalog_1.hash"):
                self.store.blobs["Android/1.4.0/wrestler_jazz/v1/product.json"] = (b"{}", "theirs")
        self.store.before_write = race
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual(b"{}", self.store.blobs["Android/1.4.0/wrestler_jazz/v1/product.json"][0])
        self.assertNotIn("Android/1.4.0/products.json", self.store.blobs)

    def test_products_json_etag_conflict_fails(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}))))
        stage = self.export({"b": ["B/1"]})
        def race(name):
            if name.endswith("b/v1/product.json"):
                body, _ = self.store.blobs["Android/1.4.0/products.json"]
                self.store.blobs["Android/1.4.0/products.json"] = (body, "changed")
        self.store.before_write = race
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual(["a"], [e["productId"] for e in self.index()["products"]])

    def test_removed_key_warns(self):
        self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1", "A/2"]})))
        run = p.Run(self.tmp / "logs" / "w.log", dry_run=True)
        p.publish(self.store, run, self.export({"a": ["A/1"]}), None)
        self.assertTrue(any("A/2" in w for w in run.warnings))
        run.summary(True)

    def test_build_changed_after_export_fails(self):
        stage = self.export({"a": ["A/1"]})
        (self.build / "catalog_1.hash").write_text("different")
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual([], self.store.writes)

    def test_rollback_to_and_drop_keep_folders(self):
        self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]})))
        self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1", "A/2"]})))
        base = ["--platform", "Android", "--app-version", "1.4.0", "--product", "a"]
        self.assertEqual(0, self.run_cmd("rollback", *base, "--to", "v1"))
        entry = self.index()["products"][0]
        self.assertEqual(("v1", ["v1", "v2"]), (entry["current"], entry["versions"]))
        self.assertEqual(0, self.run_cmd("rollback", *base, "--drop", "v2"))
        self.assertEqual(["v1"], self.index()["products"][0]["versions"])
        self.assertIn("Android/1.4.0/a/v2/product.json", self.store.blobs)
        self.assertEqual(1, self.run_cmd("rollback", *base, "--drop", "v1"))

    def test_product_without_keys_is_published(self):
        stage = self.export({"animation_pack_taunt": [], "a": ["A/1"]})
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage)))
        manifest = json.loads(self.store.blobs["Android/1.4.0/animation_pack_taunt/v1/product.json"][0])
        self.assertEqual([], manifest["keys"])
        self.assertEqual({"a", "animation_pack_taunt"}, {e["productId"] for e in self.index()["products"]})

    def test_invalid_keys_fail(self):
        stage = self.export({"a": ["A/1"]})
        doc = json.loads((stage / "a" / "product.json").read_text())
        doc["keys"] = ["A/1", ""]
        (stage / "a" / "product.json").write_text(json.dumps(doc))
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual([], self.store.writes)

    def retarget(self, stage, catalog_file=None, catalog_build=None):
        export = json.loads((stage / "export.json").read_text())
        if catalog_file:
            export.update(catalogFile=catalog_file, hashFile=catalog_file.rsplit(".", 1)[0] + ".hash")
        if catalog_build:
            export["catalogBuild"] = catalog_build
            for pid in export["products"]:
                doc = json.loads((stage / pid / "product.json").read_text())
                doc["catalogBuild"] = catalog_build
                (stage / pid / "product.json").write_text(json.dumps(doc))
        (stage / "export.json").write_text(json.dumps(export))
        return stage

    def test_full_rebuild_with_new_catalog_name_fails(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}))))
        # A full rebuild of the same app version: new catalog name and a new bundle.
        (self.build / "catalog_1.bin").rename(self.build / "catalog_2.bin")
        (self.build / "catalog_1.hash").rename(self.build / "catalog_2.hash")
        (self.build / "new_assets_all_aa11.bundle").write_bytes(b"bundle-b")
        stage = self.retarget(self.export({"a": ["A/1"]}), catalog_file="catalog_2.bin")
        writes = len(self.store.writes)

        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage), "--dry-run"))
        self.assertEqual(writes, len(self.store.writes))  # nothing uploaded, not even bundles

        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage), "--allow-new-catalog"))
        self.assertIn("Android/1.4.0/catalog_2.bin", self.store.blobs)
        self.assertIn("Android/1.4.0/catalog_1.bin", self.store.blobs)  # old one left in place

    def test_content_update_with_same_catalog_name_publishes(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}))))
        (self.build / "catalog_1.hash").write_text("abc124")  # content update: same name, new hash
        stage = self.retarget(self.export({"a": ["A/1", "A/2"]}), catalog_build="abc124")
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual(b"abc124", self.store.blobs["Android/1.4.0/catalog_1.hash"][0])

    def test_first_publish_to_empty_folder_has_no_catalog_check(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]})), "--dry-run"))

    def test_unknown_product_filter_fails(self):
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]})), "--product", "zzz"))

    def test_failed_bundle_keeps_going_then_rerun_resumes(self):
        (self.build / "other_assets_all_bb22.bundle").write_bytes(b"bundle-b")
        stage = self.export({"a": ["A/1"]})
        def broken(name):
            if name.endswith("skins_assets_all_ff00.bundle"):
                raise ConnectionError("network down")
        self.store.before_write = broken
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertIn("Android/1.4.0/other_assets_all_bb22.bundle", self.store.blobs)  # kept going
        self.assertNotIn("Android/1.4.0/catalog_1.bin", self.store.blobs)  # stopped before the catalog
        self.assertNotIn("Android/1.4.0/products.json", self.store.blobs)

        self.store.before_write = None
        self.store.writes.clear()
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage)))
        self.assertNotIn("Android/1.4.0/other_assets_all_bb22.bundle", self.store.writes)  # skipped, identical
        self.assertIn("Android/1.4.0/skins_assets_all_ff00.bundle", self.store.writes)
        self.assertEqual("v1", self.index()["products"][0]["current"])

    def test_transient_failure_is_retried(self):
        failures = []
        def flaky(name):
            if name.endswith(".bundle") and not failures:
                failures.append(name)
                raise TimeoutError("timed out")
        self.store.before_write = flaky
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}))))
        self.assertEqual(1, len(failures))
        self.assertIn("Android/1.4.0/skins_assets_all_ff00.bundle", self.store.blobs)

    def test_rerun_reuses_manifest_from_interrupted_publish(self):
        stage = self.export({"a": ["A/1"]})
        def interrupt(name):
            if name.endswith("products.json"):
                raise ConnectionError("connection reset")
        self.store.before_write = interrupt
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertIn("Android/1.4.0/a/v1/product.json", self.store.blobs)
        self.assertNotIn("Android/1.4.0/products.json", self.store.blobs)

        self.store.before_write = None
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage)))
        self.assertNotIn("Android/1.4.0/a/v2/product.json", self.store.blobs)
        entry = self.index()["products"][0]
        self.assertEqual(("v1", ["v1"]), (entry["current"], entry["versions"]))

    def test_changed_draft_after_interruption_takes_next_number(self):
        def interrupt(name):
            if name.endswith("products.json"):
                raise ConnectionError("connection reset")
        self.store.before_write = interrupt
        self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]})))
        self.store.before_write = None
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1", "A/2"]}))))
        self.assertEqual("v2", self.index()["products"][0]["current"])

    def test_progress_lines(self):
        import contextlib, io
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]})), "--progress"))
        events = [json.loads(l[len("@@progress "):]) for l in out.getvalue().splitlines() if l.startswith("@@progress ")]
        self.assertEqual([1, 2, 3, 4], sorted({e["step"] for e in events}))
        last = events[-1]
        self.assertEqual((4, 1, 1), (last["step"], last["done"], last["total"]))
        bundles = [e for e in events if e["step"] == 1]
        self.assertEqual(bundles[-1]["total"], bundles[-1]["done"])
        log = next((self.tmp / "logs").glob("publish_*.log")).read_text(encoding="utf-8")
        self.assertNotIn("@@progress", log)
    BASELINE = "_build-state/Android/1.4.0/addressables_content_state.bin"

    def test_baseline_is_stored_once(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}, b"state-1"))))
        self.assertEqual(b"state-1", self.store.blobs[self.BASELINE][0])
        w = self.store.writes
        self.assertLess(w.index("Android/1.4.0/catalog_1.hash"), w.index(self.BASELINE))
        self.assertLess(w.index(self.BASELINE), w.index("Android/1.4.0/a/v1/product.json"))

        # A content update exports the same file: skipped, not rewritten.
        self.store.writes.clear()
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1", "A/2"]}, b"state-1"))))
        self.assertNotIn(self.BASELINE, self.store.writes)

    def test_different_baseline_fails_before_writing(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}, b"state-1"))))
        writes = len(self.store.writes)
        stage = self.export({"a": ["A/1"]}, b"state-2")
        self.assertEqual(1, self.run_cmd("publish", "--staging", str(stage)))
        self.assertEqual(writes, len(self.store.writes))
        self.assertEqual(b"state-1", self.store.blobs[self.BASELINE][0])

        self.assertEqual(0, self.run_cmd("publish", "--staging", str(stage), "--allow-new-catalog"))
        self.assertEqual(b"state-2", self.store.blobs[self.BASELINE][0])

    def test_export_without_baseline_warns_only_when_none_stored(self):
        run = p.Run(self.tmp / "logs" / "a.log", dry_run=False)
        p.publish(self.store, run, self.export({"a": ["A/1"]}), None)
        run.summary(True)
        self.assertTrue(any("No content-state baseline" in w for w in run.warnings))
        self.assertNotIn(self.BASELINE, self.store.blobs)

        self.store.blobs[self.BASELINE] = (b"state-1", "e1")
        run = p.Run(self.tmp / "logs" / "b.log", dry_run=False)
        p.publish(self.store, run, self.export({"a": ["A/1", "A/2"]}), None)
        run.summary(True)
        self.assertFalse(any("No content-state baseline" in w for w in run.warnings))

    def test_baseline_dry_run_writes_nothing(self):
        self.assertEqual(0, self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}, b"state-1")), "--dry-run"))
        self.assertEqual([], self.store.writes)

    def test_download_baseline(self):
        out = self.tmp / "dl" / "addressables_content_state.bin"
        base = ["baseline", "--platform", "Android", "--app-version", "1.4.0", "--out", str(out)]
        self.assertEqual(1, self.run_cmd(*base))
        self.assertFalse(out.exists())
        self.store.blobs[self.BASELINE] = (b"state-1", "e1")
        self.assertEqual(0, self.run_cmd(*base))
        self.assertEqual(b"state-1", out.read_bytes())
    def test_status_state_json(self):
        import contextlib, io
        base = ["status", "--platform", "Android", "--app-version", "1.4.0", "--state-json"]
        def state():
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(0, self.run_cmd(*base))
            line = next(l for l in out.getvalue().splitlines() if l.startswith("@@state "))
            return json.loads(line[len("@@state "):])
        self.assertEqual({"catalogs": [], "baseline": False, "products": 0}, state())
        self.run_cmd("publish", "--staging", str(self.export({"a": ["A/1"]}, b"state-1")))
        self.assertEqual({"catalogs": ["catalog_1.bin"], "baseline": True, "products": 1}, state())

if __name__ == "__main__":
    unittest.main()
