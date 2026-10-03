import shutil
import tempfile
import unittest
from pathlib import Path

from sign import FINGERPRINT, gpg, import_public, keyring, sign_distribution, verify_signatures
from verify import expected_names


@unittest.skipUnless(shutil.which("gpg"), "GnuPG is required for signature integration tests")
class SigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with keyring() as home:
            gpg(home, "--pinentry-mode", "loopback", "--passphrase", "",
                "--quick-generate-key", "Nexa test <test@example.invalid>", "ed25519", "sign", "0")
            listing = gpg(home, "--with-colons", "--fingerprint", "--list-keys")
            cls.fingerprint = next(line.split(b":")[9].decode() for line in listing.splitlines()
                                   if line.startswith(b"fpr:"))
            cls.public = gpg(home, "--armor", "--export", cls.fingerprint)
            cls.private = gpg(home, "--armor", "--export-secret-keys", cls.fingerprint).decode()

    def test_signed_distribution_and_rerun_reject_tampering(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            names = sorted(expected_names("2.0.0"))
            for name in names:
                (root / name).write_bytes(b"package fixture")
            def sign():
                sign_distribution(root, "2.0.0", self.public, self.private, "", self.fingerprint)
            def check():
                verify_signatures(root, "2.0.0", self.public, self.fingerprint)
            sign()
            self.assertEqual(40, len(list(root.iterdir())))
            sign()
            check()
            for name in (names[0], "SHA256SUMS", "Nexa-Release.json", names[0] + ".asc"):
                original = (root / name).read_bytes()
                (root / name).write_bytes(b"tampered")
                with self.assertRaises(ValueError):
                    check()
                (root / name).write_bytes(original)
            signature = root / (names[0] + ".asc")
            signature.unlink()
            with self.assertRaises(ValueError):
                check()
            sign()
            (root / "unexpected.asc").write_bytes(b"extra")
            with self.assertRaises(ValueError):
                sign()
            (root / "unexpected.asc").unlink()
            (root / names[0]).unlink()
            with self.assertRaises(ValueError):
                sign()

    def test_missing_secret_and_wrong_identity_fail_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in expected_names("2.0.0"):
                (root / name).write_bytes(b"fixture")
            with self.assertRaises(ValueError):
                sign_distribution(root, "2.0.0", self.public, "", "", self.fingerprint)
            with self.assertRaises(ValueError):
                sign_distribution(root, "2.0.0", self.public, self.private, "", FINGERPRINT)
            self.assertFalse(list(root.glob("*.asc")))

    def test_production_pin_matches_runtime_and_repository_key(self):
        repo = Path(__file__).resolve().parents[2]
        workflow = (repo / ".github/workflows/launcher-build.yml").read_text(encoding="utf-8")
        self.assertIn("pattern: Nexa-*", workflow)
        self.assertIn("name: Complete-Nexa-distribution\n          overwrite: true", workflow)
        self.assertNotIn("name: Nexa-complete-distribution", workflow)
        runtime = (repo / "Nexa.Services.Updates/Updates/UpdateGpgVerifier.cs").read_text(encoding="utf-8")
        self.assertIn('ReleaseKeyFingerprint = "' + FINGERPRINT + '"', runtime)
        with keyring() as home:
            import_public(home, (repo / "GPG-PUBLIC-KEY.asc").read_bytes(), FINGERPRINT)

    def test_differential_assets_join_signing_and_tampering_is_rejected(self):
        import json
        from delta import INDEX_NAME, delta_name, digest
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            version = "2.0.0.alpha.6"
            for name in expected_names(version):
                (root / name).write_bytes(b"full package" * 1024)
            name = delta_name(version, "win-x64", "2.0.0.alpha.5")
            (root / name).write_bytes(b"small signed delta fixture")
            index = dict(schemaVersion=1, product="nexacl", version=version, runtimeVariant="nativeaot-self-contained",
                         configuration="Release", patches=[dict(fromVersion="2.0.0.alpha.5", rid="win-x64", name=name,
                            size=(root / name).stat().st_size, sha256=digest(root / name),
                            targetSha256=digest(root / f"Nexa-{version}-win-x64.portable.zip"))])
            (root / INDEX_NAME).write_text(json.dumps(index))
            sign_distribution(root, version, self.public, self.private, "", self.fingerprint)
            verify_signatures(root, version, self.public, self.fingerprint)
            self.assertTrue((root / (INDEX_NAME + ".asc")).is_file())
            self.assertTrue((root / (name + ".asc")).is_file())
            original = (root / name).read_bytes()
            (root / name).write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                verify_signatures(root, version, self.public, self.fingerprint)
            (root / name).write_bytes(original)
            (root / (INDEX_NAME + ".asc")).write_bytes(b"wrong signature")
            with self.assertRaises(ValueError):
                verify_signatures(root, version, self.public, self.fingerprint)
