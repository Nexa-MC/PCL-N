"""These synthetic records test admission, and are NOT Minecraft launch evidence."""
import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import verify


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.policy = verify.load_policy()
        self.case = self.policy["cases"][0]
        self.commit = "a" * 40
        artifacts = []
        for kind in ("world-visual", "host-observation"):
            content = b"synthetic validator fixture, never a compatibility result"
            name = kind + ".txt"
            (self.root / name).write_bytes(content)
            artifacts.append(dict(kind=kind, path=name, sha256=hashlib.sha256(content).hexdigest()))
        self.record = dict(schema=1, case_id=self.case["id"], commit=self.commit,
                           review=dict(approved=True, reviewer="fixture"), result="passed",
                           kind="real-minecraft-world", environment="physical-desktop", native_host=True,
                           authenticated_session=True, forced_stop=False, exit_code=0, loader_version="vanilla",
                           phases_seconds={"host-connected": 0, "client-ready": 10, "world-entered": 20,
                                           "world-left": 620, "normal-exit": 630}, artifacts=artifacts)
        self.record.update({k: self.case[k] for k in ("minecraft", "loader", "java", "platform")})

    def check(self, record):
        return verify.verify_record(record, self.case, self.commit, self.root, 600)

    def test_admission_binds_target_commit_phases_and_artifacts(self):
        self.assertEqual("verified", self.check(self.record))
        for key, value in (("commit", "b" * 40), ("java", 17), ("platform", "osx-arm64"),
                           ("kind", "unverified-client"), ("environment", "container"),
                           ("forced_stop", True), ("exit_code", -1), ("exit_code", False),
                           ("native_host", False), ("authenticated_session", False)):
            record = copy.deepcopy(self.record)
            record[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.check(record)
        for value in (619, -1, True, float("inf")):
            record = copy.deepcopy(self.record)
            record["phases_seconds"]["world-left"] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.check(record)
        (self.root / "world-visual.txt").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            self.check(self.record)

    def test_missing_evidence_remains_pending(self):
        results = verify.coverage(self.policy, self.root, self.commit)
        self.assertEqual({"pending"}, set(results.values()))
        (self.root / "run.json").write_text(json.dumps(self.record))
        results = verify.coverage(self.policy, self.root, self.commit)
        self.assertEqual("verified", results[self.case["id"]])
        self.assertEqual(len(results) - 1, list(results.values()).count("pending"))
        (self.root / "duplicate.json").write_text(json.dumps(self.record))
        with self.assertRaises(ValueError):
            verify.coverage(self.policy, self.root, self.commit)

    def test_exclusions_require_current_review_and_reason(self):
        record = copy.deepcopy(self.record)
        record.update(result="not-applicable", reason="No supported native Loader build exists on this platform.")
        self.assertEqual("not-applicable", self.check(record))
        record["review"]["approved"] = False
        with self.assertRaises(ValueError):
            self.check(record)
        record["review"]["approved"] = True
        record["reason"] = "skip"
        with self.assertRaises(ValueError):
            self.check(record)

    def test_artifacts_cannot_escape_or_use_links(self):
        for path in ("../private", "/private", "a//b", "a/./b", "a/../b", "C:/private", "a\\b"):
            record = copy.deepcopy(self.record)
            record["artifacts"][0]["path"] = path
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.check(record)
        (self.root / "link").symlink_to(self.root, target_is_directory=True)
        record = copy.deepcopy(self.record)
        record["artifacts"][0]["path"] = "link/world-visual.txt"
        with self.assertRaises(ValueError):
            self.check(record)

    def test_json_duplicates_and_nonfinite_values_rejected(self):
        path = self.root / "bad.json"
        for content in ('{"schema":1,"schema":2}', '{"time":NaN}', '{"time":Infinity}'):
            path.write_text(content)
            with self.subTest(content=content), self.assertRaises(ValueError):
                verify.read_json(path)


if __name__ == "__main__":
    unittest.main()
