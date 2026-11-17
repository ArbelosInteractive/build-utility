"""Tests for check_live.py's verdicts. Run: python -m unittest test_check_live"""
import datetime
import json
import unittest

import check_live as c

NOW = datetime.datetime(2026, 11, 17, 12, 0, tzinfo=datetime.timezone.utc)


class FakeStores:
    def __init__(self, google=None, apple=None, playfab=None, errors=None):
        self.g, self.a, self.p = google or {}, apple or {}, playfab or {}
        self.errors = errors or {}

    def google(self, pid):
        return None if "google" in self.errors else ("found", self.g.get(pid))

    def apple(self, pid):
        return None if "apple" in self.errors else ("found", self.a.get(pid, (None, None)))

    def playfab(self, target, pid):
        return None if "playfab-" + target in self.errors else ("found", self.p.get((target, pid), (None, None)))


ACTIVE = {"purchaseOptions": [{"state": "ACTIVE", "regionalPricingAndAvailabilityConfigs": [
    {"regionCode": "US", "availability": "AVAILABLE"}, {"regionCode": "CA", "availability": "AVAILABLE"}]}]}
APPROVED = ({"id": "1", "attributes": {"state": "APPROVED"}}, 175)
PUBLISHED = ({"StartDate": "2026-11-01T00:00:00Z"}, None)


class VerdictTests(unittest.TestCase):
    def test_google(self):
        self.assertEqual(c.FAIL, c.google_cell(None)["status"])
        self.assertEqual((c.OK, "ACTIVE, 2 regions"), tuple(c.google_cell(ACTIVE).values()))
        self.assertEqual(c.FAIL, c.google_cell({"purchaseOptions": [{"state": "DRAFT"}]})["status"])
        self.assertEqual(c.FAIL, c.google_cell({"purchaseOptions": [{"state": "ACTIVE", "regionalPricingAndAvailabilityConfigs": [
            {"regionCode": "US", "availability": "NO_LONGER_AVAILABLE"}]}]})["status"])

    def test_apple(self):
        self.assertEqual(c.FAIL, c.apple_cell(None)["status"])
        self.assertEqual(c.OK, c.apple_cell(*APPROVED)["status"])
        self.assertEqual(c.FAIL, c.apple_cell(APPROVED[0], 0)["status"])
        self.assertEqual(c.WARN, c.apple_cell({"attributes": {"state": "WAITING_FOR_REVIEW"}})["status"])
        self.assertEqual(c.FAIL, c.apple_cell({"attributes": {"state": "MISSING_METADATA"}})["status"])

    def test_playfab_window(self):
        self.assertEqual("not in the catalog", c.playfab_cell(None, None, NOW)["detail"])
        self.assertEqual("draft only, not published", c.playfab_cell(None, {}, NOW)["detail"])
        self.assertEqual(c.OK, c.playfab_cell({"StartDate": "2026-11-01T00:00:00.1234567Z"}, None, NOW)["status"])
        self.assertEqual(c.WARN, c.playfab_cell({"StartDate": "2026-12-01T00:00:00Z"}, None, NOW)["status"])
        self.assertEqual(c.FAIL, c.playfab_cell({"EndDate": "2026-11-17T11:00:00+00:00"}, None, NOW)["status"])
        self.assertEqual(c.FAIL, c.playfab_cell({"IsHidden": True}, None, NOW)["status"])
        self.assertEqual(c.OK, c.playfab_cell({"StartDate": "2026-11-17T06:00:00-05:00"}, None, NOW)["status"])  # 11:00 UTC

    def test_content_and_data(self):
        self.assertEqual(c.OK, c.content_cell(True, {"Android": False})["status"])
        self.assertEqual(c.OK, c.content_cell(False, {"Android": True, "iOS": True})["status"])
        self.assertEqual(c.FAIL, c.content_cell(False, {"Android": True, "iOS": False})["status"])
        self.assertEqual(c.WARN, c.content_cell(False, {"Android": False, "iOS": False})["status"])
        self.assertEqual(c.WARN, c.content_cell(False, {"Android": None, "iOS": True})["status"])
        self.assertEqual(c.WARN, c.content_cell(False, {"Android": True, "iOS": c.UNPUBLISHED})["status"])
        self.assertEqual(c.FAIL, c.content_cell(False, {"Android": False, "iOS": True, "WebGL": c.UNPUBLISHED})["status"])
        self.assertIn("nothing published for iOS", c.content_cell(False, {"iOS": c.UNPUBLISHED})["detail"])
        self.assertEqual(c.OK, c.data_cell("a", set(), {"a"})["status"])
        self.assertEqual(c.OK, c.data_cell("a", {"a"}, None)["status"])
        self.assertEqual(c.FAIL, c.data_cell("a", set(), {"b"})["status"])
        self.assertEqual(c.FAIL, c.data_cell("a", set(), None)["status"])

    def test_check_combines_everything(self):
        stores = FakeStores(google={"w": ACTIVE}, apple={"w": APPROVED},
                            playfab={("staging", "w"): PUBLISHED, ("production", "w"): PUBLISHED})
        container = ({"Android": {"w"}, "iOS": {"w"}}, {"w"}, [])
        (pid, cells), = c.check({"w": 1}, set(), stores, container, container, NOW)
        self.assertTrue(all(cells[k]["status"] == c.OK for k in c.COLUMNS), cells)

        stores.errors = {"apple": "no key"}
        (_, cells), = c.check({"w": 1}, set(), stores, container, ({"Android": set(), "iOS": set()}, None, []), NOW)
        self.assertEqual(c.WARN, cells["apple"]["status"])
        self.assertIn("no key", cells["apple"]["detail"])
        self.assertEqual("not in products.json (fine only if its content ships in the app)", cells["dlc-prod"]["detail"])
        self.assertEqual(c.FAIL, cells["data-prod"]["status"])

        (_, cells), = c.check({"gems": 0}, {"gems"}, FakeStores(), container, container, NOW)
        self.assertEqual("n/a (consumable)", cells["pf-prod"]["detail"])

    def test_read_container(self):
        files = {"B/Android/1.0/products.json": json.dumps({"products": [{"productId": "w"}]}),
                 "B/data/1.0/IAPProductCatalog.json": json.dumps({"products": [{"id": "w", "type": 1}]})}
        def get(url):
            if "iOS" in url:
                raise IOError("timeout")
            return files.get(url)
        listed, remote, notes = c.read_container("B", ["Android", "iOS"], "1.0", get)
        self.assertEqual({"Android": {"w"}, "iOS": None}, listed)
        self.assertEqual({"w"}, remote)
        self.assertEqual(1, len(notes))
        listed, remote, _ = c.read_container("B", ["Android"], "2.0", get)
        self.assertEqual(({"Android": c.UNPUBLISHED}, None), (listed, remote))


if __name__ == "__main__":
    unittest.main()
