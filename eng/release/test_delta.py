import copy
import hashlib
import json
import random
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

from delta import (INDEX_NAME, MAX_HISTORY_SOURCES, MAX_PATCHES, RIDS, build_bundle, canonical_order, delta_name, digest,
                   generate, package_name, safe_path, unpack, validate_index)
from delta_history import validate_remote_manifest
from manifest import build_manifest
from verify import expected_names, verify


class DeltaTests(unittest.TestCase):
    def test_shifted_native_binary_reuses_content_and_checks_complete_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source, target = root / "source", root / "target"
            source.mkdir(); target.mkdir()
            data = random.Random(42).randbytes(5 * 1024 * 1024)
            (source / "Nexa.Desktop.exe").write_bytes(data)
            (source / "Nexa.Jvm.Host.exe").write_bytes(data[:256 * 1024])
            (source / "deleted.txt").write_text("old")
            (target / "Nexa.Desktop.exe").write_bytes(b"inserted header" + data)
            (target / "Nexa.Jvm.Host.exe").write_bytes(data[:256 * 1024])
            (target / "new.txt").write_text("new")
            (target / "empty.txt").touch()
            files = {p.name: dict(path=p.name, size=p.stat().st_size, sha256=digest(p), executable=False)
                     for p in target.iterdir()}
            output = root / "delta.zip"
            reused = build_bundle(source, target, files, output, "2.0.0.alpha.6", "2.0.0.alpha.5", "win-x64", "a" * 64)
            self.assertGreater(reused, 3 * 1024 * 1024)
            self.assertLess(output.stat().st_size, len(data) // 2)
            with zipfile.ZipFile(output) as zip:
                manifest = json.loads(zip.read("manifest.json"))
                self.assertNotIn("deleted.txt", {f["path"] for f in manifest["files"]})
            from delta import verify_bundle
            (source / "Nexa.Jvm.Host.exe").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                verify_bundle(output, source, files, manifest)

    def make_archive(self, directory, version, rid, data):
        path = directory / package_name(version, rid)
        names = ["Nexa.Desktop.exe", "Nexa.Jvm.Host.exe"] if rid.startswith("win-") else ["Nexa.Desktop", "Nexa.Jvm.Host"]
        if rid.startswith("win-"):
            with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
                for name in names:
                    archive.writestr(name, data)
        else:
            import io
            with tarfile.open(path, "w:gz") as archive:
                for name in names:
                    prefix = "Nexa/" if rid.startswith("linux-") else "Nexa.app/Contents/MacOS/"
                    item = tarfile.TarInfo(prefix + name)
                    item.size = len(data); item.mode = 0o755
                    archive.addfile(item, io.BytesIO(data))
        return path

    def test_generation_publishes_verified_smaller_deltas_for_all_six_platforms(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output, history = root / "out", root / "history"
            output.mkdir(); history.mkdir()
            previous = history / "2.0.0.alpha.5"; previous.mkdir()
            old = random.Random(21).randbytes(256 * 1024)
            for name in expected_names("2.0.0.alpha.6"):
                (output / name).write_bytes(b"native installer")
            for rid in RIDS:
                self.make_archive(previous, previous.name, rid, old)
                self.make_archive(output, "2.0.0.alpha.6", rid, old + b"new tail")
            generate(output, "2.0.0.alpha.6", history)
            names = validate_index(output, "2.0.0.alpha.6")
            self.assertEqual(7, len(names))
            verify(output, "2.0.0.alpha.6")
            self.assertEqual(25, len((output / "SHA256SUMS").read_text().splitlines()))
            raw = (output / INDEX_NAME).read_text()
            index = json.loads(raw)
            broken = copy.deepcopy(index)
            broken["patches"][0]["targetSha256"] = "0" * 64
            (output / INDEX_NAME).write_text(json.dumps(broken))
            with self.assertRaises(ValueError):
                validate_index(output, "2.0.0.alpha.6")
            (output / INDEX_NAME).write_text(raw)
            bundle = output / index["patches"][0]["name"]
            bundle.write_bytes(b"corrupt")
            with self.assertRaises(ValueError):
                validate_index(output, "2.0.0.alpha.6")

    def test_no_savings_omits_bundle_and_preserves_full_only_release(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output, history = root / "out", root / "history"
            output.mkdir(); history.mkdir(); previous = history / "2.0.0.alpha.5"; previous.mkdir()
            for rid in RIDS:
                self.make_archive(previous, previous.name, rid, b"old")
                self.make_archive(output, "2.0.0.alpha.6", rid, b"new")
            generate(output, "2.0.0.alpha.6", history)
            self.assertFalse((output / INDEX_NAME).exists())
            self.assertFalse(list(output.glob("*.delta.zip")))

    def test_five_sources_generate_thirty_entries_and_thirty_one_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); output, history = root / "out", root / "history"
            output.mkdir(); history.mkdir()
            version = "2.0.0.alpha.6"
            self.assertEqual(5, MAX_HISTORY_SOURCES)
            self.assertEqual(30, MAX_PATCHES)
            data = random.Random(21).randbytes(256 * 1024)
            for name in expected_names(version):
                (output / name).write_bytes(b"installer fixture")
            for number in range(1, 6):
                previous = history / f"2.0.0.alpha.{number}"; previous.mkdir()
                for rid in RIDS:
                    self.make_archive(previous, previous.name, rid, data)
            for rid in RIDS:
                self.make_archive(output, version, rid, data + b"new tail")
            generate(output, version, history)
            index_path = output / INDEX_NAME
            index = json.loads(index_path.read_text())
            self.assertEqual(30, len(index["patches"]))
            self.assertEqual(31, len(validate_index(output, version)))
            verify(output, version)
            self.assertEqual(49, len((output / "SHA256SUMS").read_text().splitlines()))
            extra = copy.deepcopy(index["patches"][0])
            source = "1.9.9.alpha.1"
            extra["fromVersion"] = source
            extra["name"] = delta_name(version, extra["rid"], source)
            (output / extra["name"]).write_bytes((output / index["patches"][0]["name"]).read_bytes())
            index["patches"].append(extra)
            index_path.write_text(json.dumps(index))
            with self.assertRaisesRegex(ValueError, "index identity"):
                validate_index(output, version)

    def test_untrusted_archive_paths_links_and_actual_size_mismatch_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in ("../escape", "/absolute", "bad\\name", "name:stream", "space ", "Nexa.app/../escape"):
                with self.assertRaises(ValueError):
                    safe_path(name, "win-x64")
            path = root / "bad.zip"
            with zipfile.ZipFile(path, "w") as archive:
                entry = zipfile.ZipInfo("link"); entry.external_attr = 0xA1FF << 16
                archive.writestr(entry, "../target")
            destination = root / "dst"; destination.mkdir()
            with self.assertRaises(ValueError):
                unpack(path, destination, "win-x64")
            with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
                archive.writestr("manifest.json", b"long content")
            raw = bytearray(path.read_bytes()); offset = raw.index(b"PK\x01\x02")
            raw[offset + 24:offset + 28] = (1).to_bytes(4, "little")
            path.write_bytes(raw)
            with self.assertRaises((ValueError, zipfile.BadZipFile)):
                unpack(path, destination, "win-x64")

    def test_history_envelope_requires_exact_signed_identity(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in expected_names("2.0.0.alpha.5"):
                (root / name).write_bytes(b"old package")
            original = build_manifest(root, "2.0.0.alpha.5")
            self.assertEqual(18, len(validate_remote_manifest(json.dumps(original).encode(), "2.0.0.alpha.5")))
            for key, value in (("product", "legacy"), ("channel", "stable"), ("version", "2.0.0.alpha.4"), ("schemaVersion", True)):
                invalid = copy.deepcopy(original); invalid[key] = value
                with self.assertRaises(ValueError):
                    validate_remote_manifest(json.dumps(invalid).encode(), "2.0.0.alpha.5")
            with self.assertRaises(ValueError):
                canonical_order("2.0.0.ci.abcdef")


