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

    def write(self, name, data, content_type, cache_control, create_only=False, etag=None):
        if self.before_write:
            self.before_write(name)
        body = data if isinstance(data, bytes) else Path(data[0]).read_bytes()
        if create_only and name in self.blobs:
            raise p.Conflict("exists")
        if etag and (name not in self.blobs or self.blobs[name][1] != etag):
            raise p.Conflict("etag")
        self.counter += 1
        self.blobs[name] = (body, f"etag{self.counter}")
        self.writes.append(name)


class PublishTests(unittest.TestCase):
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

    def export(self, products):
        stage = self.tmp / f"stage{len(list(self.tmp.glob('stage*')))}"
        stage.mkdir()
        (stage / "export.json").write_text(json.dumps({
            "schemaVersion": 1, "kind": "dlc-content-export", "platform": "Android", "appVersion": "1.4.0",
            "catalogFile": "catalog_1.bin", "hashFile": "catalog_1.hash", "catalogBuild": "abc123",
            "addressablesBuildPath": str(self.build), "products": list(products)}))
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


if __name__ == "__main__":
    unittest.main()
