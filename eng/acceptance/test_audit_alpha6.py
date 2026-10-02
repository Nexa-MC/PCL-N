import copy
import json
from pathlib import Path
import tempfile
import unittest

import audit_alpha6


class Alpha6AuditTests(unittest.TestCase):
    def test_repository_ledger_is_valid_and_not_accepted(self):
        ledger = audit_alpha6.load_and_validate(audit_alpha6.DEFAULT_LEDGER)
        self.assertEqual("not-accepted", ledger["overall"])
        self.assertEqual(9, len(ledger["items"]))
        self.assertFalse(any(item["state"] == "accepted" for item in ledger["items"]))

    def test_accepted_item_cannot_retain_open_work(self):
        ledger = json.loads(audit_alpha6.DEFAULT_LEDGER.read_text(encoding="utf-8"))
        ledger["items"] = [copy.deepcopy(ledger["items"][0])]
        ledger["items"][0]["state"] = "accepted"
        ledger["overall"] = "accepted"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ledger.json"
            path.write_text(json.dumps(ledger), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "accepted but still has remaining work"):
                audit_alpha6.load_and_validate(path)

    def test_require_accepted_fails_for_open_ledger(self):
        self.assertEqual(1, audit_alpha6.main(["--require-accepted"]))


if __name__ == "__main__":
    unittest.main()
