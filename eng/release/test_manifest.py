import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from manifest import MANIFEST_NAME, release_channel, validate_manifest
from verify import expected_names, verify


class ReleaseManifestTests(unittest.TestCase):
    def test_runtime_fixture_matches_the_publisher_contract(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            version = "2.0.0.alpha.6"
            for name in expected_names(version):
                (root / name).write_bytes(b"publisher fixture package\n")
            verify(root, version)
            fixture = Path(__file__).resolve().parents[2] / "tests/Nexa.Services.Tests/Fixtures/ReleaseManifest.json"
            self.assertEqual(json.loads((root / MANIFEST_NAME).read_bytes()), json.loads(fixture.read_bytes()))

    def seed(self, directory, version="2.0.0.alpha.6"):
        for name in expected_names(version):
            (directory / name).write_bytes((name + "\nfixture").encode())
        verify(directory, version)
        return json.loads((directory / MANIFEST_NAME).read_bytes())

    def test_manifest_binds_every_actual_asset_and_derived_identity(self):
        for version, channel in (("2.0.0.alpha.6", "alpha"), ("2.0.0.beta.1", "beta"),
                                 ("2.0.0", "stable"), ("2.0.0.ci.abcdef", "ci")):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                data = self.seed(root, version)
                self.assertEqual(channel, data["channel"])
                self.assertEqual("Release", data["configuration"])
                self.assertEqual("nativeaot-self-contained", data["runtimeVariant"])
                self.assertEqual(expected_names(version), {a["name"] for a in data["assets"]})
                for asset in data["assets"]:
                    raw = (root / asset["name"]).read_bytes()
                    self.assertEqual(len(raw), asset["size"])
                    self.assertEqual(hashlib.sha256(raw).hexdigest(), asset["sha256"])
                    self.assertEqual(f"Nexa-{version}-{asset['rid']}.{asset['format']}", asset["name"])
                validate_manifest(root, version)
                # JSON formatting and asset order do not change the authenticated identities.
                data["assets"].reverse()
                (root / MANIFEST_NAME).write_text(json.dumps(data, indent=2))
                validate_manifest(root, version)
        for version in ("v2.0.0", "2.0.0-alpha.6", "2.0.0.alpha.0", "2.0.0.ci.ABCDEF", "02.0.0", "9223372036854775808.0.0"):
            with self.assertRaises(ValueError):
                release_channel(version)

    def test_independent_validation_rejects_identity_schema_and_asset_mutations(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original = self.seed(root)
            variants = []
            for key, value in (("schemaVersion", True), ("product", "pcl"), ("version", "2.0.0.beta.1"),
                               ("channel", "stable"), ("configuration", "CI"),
                               ("runtimeVariant", "SelfContained"), ("url", "https://example.invalid")):
                data = copy.deepcopy(original)
                data[key] = value
                variants.append(data)
            for key, value in (("rid", "win-arm64"), ("format", "other"), ("size", True),
                               ("size", 1.5), ("sha256", "0" * 64), ("name", "../payload"), ("url", "file:///tmp/payload")):
                data = copy.deepcopy(original)
                data["assets"][0][key] = value
                variants.append(data)
            data = copy.deepcopy(original)
            data["assets"][1] = copy.deepcopy(data["assets"][0])
            variants.append(data)
            data = copy.deepcopy(original)
            data["assets"].pop()
            variants.append(data)
            for data in variants:
                (root / MANIFEST_NAME).write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    validate_manifest(root, original["version"])
            (root / MANIFEST_NAME).write_text(json.dumps(original).replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
            with self.assertRaises(ValueError):
                validate_manifest(root, original["version"])
            (root / MANIFEST_NAME).write_bytes(b" " * (1024 * 1024 + 1))
            with self.assertRaises(ValueError):
                validate_manifest(root, original["version"])
            (root / MANIFEST_NAME).write_text(json.dumps(original))
            (root / original["assets"][0]["name"]).write_bytes(b"modified")
            with self.assertRaises(ValueError):
                validate_manifest(root, original["version"])
