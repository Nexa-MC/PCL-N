import copy
import hashlib
import io
import json
import tempfile
import unittest
import urllib.error
from contextlib import ExitStack, nullcontext
from pathlib import Path
from unittest.mock import patch

import delta_history as history
from delta import MAX_PACKAGE, RIDS, package_name
from verify import expected_names


CURRENT = "2.0.0.alpha.6"
PREVIOUS = "2.0.0.alpha.5"


def record(version, identifier=1, draft=False):
    return dict(id=identifier, tag_name=version, draft=draft)


class HistorySelectionTests(unittest.TestCase):
    def select(self, records):
        return history.recent_history_candidates(json.dumps(records).encode(), CURRENT)

    def five(self):
        return [record("v2.0.0.alpha." + str(i), 10 + i) for i in range(5, 0, -1)]

    def expected_five(self):
        return [("2.0.0.alpha." + str(i), "v2.0.0.alpha." + str(i)) for i in range(5, 0, -1)]

    def test_current_present_with_or_without_v_leaves_five_history_slots(self):
        for current in (CURRENT, "v" + CURRENT):
            with self.subTest(current=current):
                self.assertEqual(self.expected_five(), self.select([record(current), *self.five()]))

    def test_current_absent_does_not_use_sixth_historical_record(self):
        records = [*self.five(), record("v1.9.9.alpha.1", 99)]
        self.assertEqual(self.expected_five(), self.select(records))

    def test_incompatible_entries_consume_slots_without_refill(self):
        records = [record("v2.0.0.beta." + str(i), i) for i in range(1, 6)]
        self.assertEqual([], self.select([*records, record("v" + PREVIOUS, 99)]))
        for incompatible in ("v2.0.0.beta.1", "v2.0.0.alpha.7", "v1.4.14", "not-a-version", "v2.0.0.ci.abcdef"):
            with self.subTest(incompatible=incompatible):
                self.assertEqual([(PREVIOUS, "v" + PREVIOUS)],
                    self.select([record(incompatible), record("v" + PREVIOUS, 2)]))

    def test_drafts_and_empty_or_current_only_windows_have_no_source(self):
        for records in ([], [record("v" + CURRENT)],
                        [record("v2.0.0.alpha.7", draft=True), record("v" + CURRENT, 2)]):
            with self.subTest(records=records):
                self.assertEqual([], self.select(records))
        self.assertEqual([(PREVIOUS, "v" + PREVIOUS)], self.select([
            record("v2.0.0.alpha.7", draft=True), record("v" + PREVIOUS, 2)]))

    def test_invalid_or_duplicate_response_is_not_silently_full_only(self):
        invalid = [{}, None, "error", [None], [record("v" + PREVIOUS, True)],
                   [dict(id=1, tag_name="v" + PREVIOUS, draft="false")],
                   [record("v" + PREVIOUS), record("v2.0.0.alpha.4")],
                   [record(PREVIOUS), record("v" + PREVIOUS, 2)],
                   [record("x" * 129)], [record("v2.0.0.alpha." + str(i), i) for i in range(1, 8)]]
        for records in invalid:
            with self.subTest(records=records), self.assertRaises(ValueError):
                self.select(records)
        for raw in (b"not json", b'[{"id":1,"id":2,"tag_name":"v2.0.0.alpha.5","draft":false}]'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                history.recent_history_candidates(raw, CURRENT)


class HistoryFetchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "GPG-PUBLIC-KEY.asc").write_bytes(b"public key fixture")
        self.output = self.root / "history"
        self.calls = []
        self.records = [record("v" + CURRENT), record("v" + PREVIOUS, 2)]
        self.raw_listing = None
        self.responses = {}
        self.listing_url = f"https://api.github.com/repos/{history.REPOSITORY}/releases?per_page=6&page=1"
        self.asset_base = f"https://github.com/{history.REPOSITORY}/releases/download/v{PREVIOUS}/"
        assets = []
        for name in sorted(expected_names(PREVIOUS)):
            content = (name + " trusted fixture").encode()
            self.responses[self.asset_base + name] = content
            rid = next(rid for rid in RIDS if f"-{rid}." in name)
            assets.append(dict(name=name, rid=rid, format=name.split(f"-{rid}.", 1)[1],
                               size=len(content), sha256=hashlib.sha256(content).hexdigest()))
        self.manifest = dict(schemaVersion=1, product="nexacl", version=PREVIOUS, channel="alpha",
                             runtimeVariant="nativeaot-self-contained", configuration="Release", assets=assets)
        self.manifest_url = self.asset_base + "Nexa-Release.json"
        self.signature_url = self.manifest_url + ".asc"
        self.responses[self.signature_url] = b"signature fixture"
        self.signature_status = f"[GNUPG:] VALIDSIG {history.FINGERPRINT} fixture\n".encode()
        self.signature_error = None

    def open(self, request, timeout):
        self.assertEqual(120, timeout)
        url = request.full_url
        self.calls.append(url)
        if url == self.listing_url:
            response = self.raw_listing if self.raw_listing is not None else json.dumps(self.records).encode()
            self.assertEqual("Bearer fixture-token", request.get_header("Authorization"))
        else:
            self.assertIsNone(request.get_header("Authorization"))
            if url == self.manifest_url and url not in self.responses:
                response = json.dumps(self.manifest).encode()
            else:
                self.assertIn(url, self.responses, "Unexpected request or scan into older history")
                response = self.responses[url]
        if isinstance(response, Exception):
            raise response
        return io.BytesIO(response)

    def fetch(self):
        with ExitStack() as stack:
            stack.enter_context(patch.object(history, "ROOT", self.root))
            stack.enter_context(patch.object(history, "keyring", return_value=nullcontext(self.root / "keyring")))
            imported = stack.enter_context(patch.object(history, "import_public"))
            stack.enter_context(patch.object(history, "gpg", return_value=self.signature_status, side_effect=self.signature_error))
            stack.enter_context(patch.object(history.urllib.request, "urlopen", side_effect=self.open))
            stack.enter_context(patch.dict(history.os.environ, {"GH_TOKEN": "fixture-token"}))
            history.fetch(self.output, CURRENT)
            imported.assert_called_once_with(self.root / "keyring", b"public key fixture", history.FINGERPRINT)

    def test_one_authenticated_source_downloads_exactly_six_packages(self):
        self.fetch()
        self.assertEqual([PREVIOUS], [path.name for path in self.output.iterdir()])
        self.assertEqual({package_name(PREVIOUS, rid) for rid in RIDS},
                         {path.name for path in (self.output / PREVIOUS).iterdir()})
        self.assertEqual(1, self.calls.count(self.listing_url))
        self.assertEqual(9, len(self.calls))  # One list, manifest, signature and six packages.

    def add_source(self, version):
        base = f"https://github.com/{history.REPOSITORY}/releases/download/v{version}/"
        manifest = copy.deepcopy(self.manifest)
        manifest["version"] = version
        for asset in manifest["assets"]:
            asset["name"] = asset["name"].replace(PREVIOUS, version)
            content = (asset["name"] + " trusted fixture").encode()
            asset["size"] = len(content)
            asset["sha256"] = hashlib.sha256(content).hexdigest()
            self.responses[base + asset["name"]] = content
        self.responses[base + "Nexa-Release.json"] = json.dumps(manifest).encode()
        self.responses[base + "Nexa-Release.json.asc"] = b"signature fixture"

    def test_all_five_sources_can_be_downloaded_for_all_six_platforms(self):
        self.records = [record("v" + CURRENT)]
        for number in range(5, 0, -1):
            version = "2.0.0.alpha." + str(number)
            self.records.append(record("v" + version, number + 10))
            self.add_source(version)
        self.fetch()
        self.assertEqual({"2.0.0.alpha." + str(i) for i in range(1, 6)},
                         {path.name for path in self.output.iterdir()})
        self.assertEqual(30, len(list(self.output.glob("*/*"))))
        self.assertEqual(41, len(self.calls))
        self.assertEqual(1, self.calls.count(self.listing_url))

    def test_missing_manifest_can_use_other_sources_only_inside_window(self):
        self.records = [record("v" + PREVIOUS), record("v2.0.0.alpha.4", 2)]
        self.responses[self.manifest_url] = urllib.error.HTTPError(self.manifest_url, 404, "missing", {}, None)
        self.add_source("2.0.0.alpha.4")
        self.fetch()
        self.assertEqual(["2.0.0.alpha.4"], [path.name for path in self.output.iterdir()])
        self.assertEqual(1, self.calls.count(self.listing_url))

    def test_five_missing_manifests_do_not_refill_from_sixth_release(self):
        self.records = []
        manifests = []
        for number in range(5, 0, -1):
            self.records.append(record("v2.0.0.alpha." + str(number), number + 10))
            url = self.manifest_url.replace(PREVIOUS, "2.0.0.alpha." + str(number))
            manifests.append(url)
            self.responses[url] = urllib.error.HTTPError(url, 404, "missing", {}, None)
        self.records.append(record("v1.9.9.alpha.1", 99))
        self.fetch()
        self.assertEqual([self.listing_url, *manifests], self.calls)
        self.assertEqual([], list(self.output.iterdir()))

    def test_incompatible_five_do_not_fetch_sixth_manifest_or_next_page(self):
        self.records = [record("v2.0.0.beta." + str(i), i) for i in range(1, 6)]
        self.records.append(record("v" + PREVIOUS, 99))
        self.fetch()
        self.assertEqual([self.listing_url], self.calls)
        self.assertEqual([], list(self.output.iterdir()))

    def test_signature_missing_or_wrong_signer_remains_an_error(self):
        self.responses[self.signature_url] = urllib.error.HTTPError(self.signature_url, 404, "missing", {}, None)
        with self.assertRaises(urllib.error.HTTPError):
            self.fetch()
        self.responses[self.signature_url] = b"signature fixture"
        self.signature_status = b"[GNUPG:] VALIDSIG WRONGKEY fixture\n"
        with self.assertRaisesRegex(ValueError, "signer"):
            self.fetch()
        self.assertFalse(any(url.endswith("portable.zip") for url in self.calls))

    def test_invalid_signed_identity_or_size_remains_an_error(self):
        for mutate in (lambda m: m.update(product="other"),
                       lambda m: m.update(version="2.0.0.alpha.4"),
                       lambda m: m["assets"][0].update(size=MAX_PACKAGE + 1)):
            original = self.manifest
            self.manifest = copy.deepcopy(original)
            mutate(self.manifest)
            with self.assertRaises(ValueError):
                self.fetch()
            self.manifest = original
        self.assertFalse(any(url.endswith("portable.zip") for url in self.calls))

    def test_cryptographic_verification_failure_is_not_swallowed(self):
        self.signature_error = ValueError("Publisher signature operation failed")
        with self.assertRaisesRegex(ValueError, "signature operation failed"):
            self.fetch()
        self.assertEqual([self.listing_url, self.manifest_url, self.signature_url], self.calls)
        self.assertEqual([], list(self.output.iterdir()))

    def test_downloaded_package_size_and_hash_are_still_checked(self):
        target = self.asset_base + package_name(PREVIOUS, RIDS[0])
        valid = self.responses[target]
        for content in (valid[:-1], b"X" * len(valid), valid + b"extra"):
            with self.subTest(length=len(content)), tempfile.TemporaryDirectory() as temp:
                self.output = Path(temp) / "history"
                self.responses[target] = content
                with self.assertRaises(ValueError):
                    self.fetch()

    def test_server_or_network_errors_are_not_treated_as_missing_history(self):
        for error in (urllib.error.HTTPError(self.manifest_url, 500, "server", {}, None),
                      urllib.error.URLError("network unavailable")):
            self.responses[self.manifest_url] = error
            with self.subTest(error=error), self.assertRaises(urllib.error.URLError):
                self.fetch()
        self.assertTrue(all(url in (self.listing_url, self.manifest_url) for url in self.calls))

    def test_large_100_record_response_is_replaced_by_one_small_bounded_request(self):
        latest = [dict(item, body="x" * 50000) for item in self.records]
        old_response = json.dumps(latest * 50).encode()
        self.assertGreater(len(old_response), history.HISTORY_LIST_BYTES)
        with patch.object(history.urllib.request, "urlopen", return_value=io.BytesIO(old_response)):
            with self.assertRaisesRegex(ValueError, "budget"):
                history.download("https://api.github.com/old-list-fixture", self.root / "old-list", history.HISTORY_LIST_BYTES)
        self.raw_listing = json.dumps(latest).encode()
        self.fetch()
        self.assertEqual(1, sum("/releases?" in url for url in self.calls))
        self.assertNotIn("per_page=100", " ".join(self.calls))

    def test_response_byte_budget_boundary_is_enforced_before_json_parsing(self):
        raw = json.dumps(self.records).encode()
        self.raw_listing = raw + b" " * (history.HISTORY_LIST_BYTES - len(raw))
        self.responses[self.manifest_url] = urllib.error.HTTPError(self.manifest_url, 404, "missing", {}, None)
        self.fetch()
        self.raw_listing += b" "
        self.calls.clear()
        with self.assertRaisesRegex(ValueError, "budget"):
            self.fetch()
        self.assertEqual([self.listing_url], self.calls)


if __name__ == "__main__":
    unittest.main()
