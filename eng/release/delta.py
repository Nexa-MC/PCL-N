"""Content-defined file deltas; no source package is executed or extracted by archive APIs."""
import argparse
import hashlib
import json
import shutil
import tarfile
import tempfile
import zipfile
from pathlib import Path

from manifest import release_channel, unique_object

INDEX_NAME = "Nexa-Delta.json"
ALGORITHM = "nexa-file-delta-v1"
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
MAX_HISTORY_SOURCES = 5
MAX_PATCHES = MAX_HISTORY_SOURCES * len(RIDS)
MAX_PACKAGE = 2 * 1024**3
MAX_FILE = 1024**3
MAX_TREE = 4 * 1024**3
MAX_MANIFEST = 16 * 1024**2
MASK64 = (1 << 64) - 1


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def canonical_order(version):
    channel = release_channel(version)
    if channel == "ci":
        raise ValueError("CI labels cannot authorize differential updates")
    parts = version.split(".")
    return (*map(int, parts[:3]), {"alpha": 0, "beta": 1, "stable": 2}[channel],
            int(parts[4]) if channel != "stable" else 0)


def package_name(version, rid):
    return f"Nexa-{version}-{rid}.portable." + ("zip" if rid.startswith("win-") else "tar.gz")


def delta_name(version, rid, source):
    return f"Nexa-{version}-{rid}.from-{source}.delta.zip"


def safe_path(name, rid):
    parts = name.split("/")
    if len(name) > 4096 or len(parts) > 32 or any(
            not part or part in (".", "..") or part.endswith((".", " "))
            or any(ord(c) < 32 or c in '<>:"\\|?*' for c in part) for part in parts):
        raise ValueError("Unsafe differential path")
    if rid.startswith("osx-") and parts[0] != "Nexa.app":
        raise ValueError("Invalid macOS bundle root")
    return parts


def copy_exact(input_stream, output, declared, maximum=MAX_FILE):
    if declared < 0 or declared > maximum:
        raise ValueError("Declared archive size exceeds budget")
    actual = 0
    while block := input_stream.read(65536):
        actual += len(block)
        if actual > declared or actual > maximum:
            raise ValueError("Actual archive bytes exceed budget")
        output.write(block)
    if actual != declared:
        raise ValueError("Declared and actual archive sizes differ")


def unpack(package, directory, rid):
    if package.stat().st_size > MAX_PACKAGE:
        raise ValueError("Archive exceeds package budget")
    seen, files, remaining = set(), {}, MAX_TREE
    count = 0

    def entry(name, is_directory, size, stream, executable):
        nonlocal remaining, count
        count += 1
        if count > 65536:
            raise ValueError("Archive entry budget exceeded")
        name = name.rstrip("/")
        parts = safe_path(name, rid)
        if name.casefold() in seen:
            raise ValueError("Duplicate archive entry")
        seen.add(name.casefold())
        target = directory.joinpath(*parts)
        if is_directory:
            if size != 0:
                raise ValueError("Directory contains archive data")
            target.mkdir(parents=True, exist_ok=True)
            return
        if size < 0 or size > remaining:
            raise ValueError("Archive tree budget exceeded")
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            copy_exact(stream, output, size)
        remaining -= size
        files[name] = dict(path=name, size=size, sha256=digest(target), executable=executable)

    if rid.startswith("win-"):
        with zipfile.ZipFile(package) as archive:
            for item in archive.infolist():
                if (item.external_attr >> 16) & 0xF000 not in (0, 0x8000, 0x4000):
                    raise ValueError("Archive contains a link or special file")
                with archive.open(item) as stream:
                    entry(item.filename, item.is_dir(), item.file_size, stream, False)
    else:
        with tarfile.open(package, "r|gz") as archive:
            for item in archive:
                if not item.isfile() and not item.isdir():
                    raise ValueError("Archive contains a link or special file")
                name = item.name
                if rid.startswith("linux-"):
                    if name.rstrip("/") == "Nexa":
                        continue
                    if not name.startswith("Nexa/"):
                        raise ValueError("Invalid Linux archive root")
                    name = name[5:]
                stream = archive.extractfile(item) if item.isfile() else None
                try:
                    entry(name, item.isdir(), item.size, stream, bool(item.mode & 0o111))
                finally:
                    if stream:
                        stream.close()
    executable = "Nexa.app/Contents/MacOS/Nexa.Desktop" if rid.startswith("osx-") else (
        "Nexa.Desktop.exe" if rid.startswith("win-") else "Nexa.Desktop")
    if executable not in files or executable.replace("Nexa.Desktop", "Nexa.Jvm.Host") not in files:
        raise ValueError("Archive lacks launcher or JVM host")
    return files


def gear(index):
    # Matches UpdateChunker / UpdateChunkProfile.V2, including unsigned overflow.
    value = (index + 0x9E3779B97F4A7C15) & MASK64
    value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
    value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & MASK64
    return value ^ (value >> 31)


GEAR = tuple(gear(i) for i in range(256))


def chunks(path):
    rolling, offset = 0, 0
    pending = bytearray()
    with path.open("rb") as stream:
        while block := stream.read(256 * 1024):
            for value in block:
                pending.append(value)
                rolling = ((rolling << 1) + GEAR[value]) & MASK64
                length = len(pending)
                if length < 128 * 1024:
                    continue
                mask = (1 << (20 if length < 512 * 1024 else 18)) - 1
                if rolling & mask and length < 1024 * 1024:
                    continue
                yield offset, bytes(pending)
                offset += length
                pending.clear()
                rolling = 0
    if pending:
        yield offset, bytes(pending)


def add_operation(operations, kind, offset, length):
    if operations and operations[-1][0] == kind and operations[-1][1] + operations[-1][2] == offset:
        operations[-1][2] += length
    else:
        operations.append([kind, offset, length])


def build_bundle(source_tree, target_tree, target_files, output, version, source_version, rid, target_sha):
    if canonical_order(source_version) >= canonical_order(version) or rid not in RIDS:
        raise ValueError("Invalid differential source identity")
    files, reused = [], 0
    with tempfile.TemporaryDirectory(prefix="nexa-literals-") as temp:
        data_path = Path(temp) / "data"
        with data_path.open("wb") as data:
            for name, identity in sorted(target_files.items()):
                safe_path(name, rid)
                old = source_tree / name
                if old.is_file() and old.stat().st_size == identity["size"] and digest(old) == identity["sha256"]:
                    operations = [["copy", 0, identity["size"]]] if identity["size"] else []
                    files.append(dict(identity, chunks=operations))
                    reused += identity["size"]
                    continue
                old_chunks = {hashlib.sha256(block).digest(): (offset, len(block))
                              for offset, block in chunks(old)} if old.is_file() else {}
                operations = []
                for _, block in chunks(target_tree / name):
                    old_chunk = old_chunks.get(hashlib.sha256(block).digest())
                    if old_chunk and old_chunk[1] == len(block):
                        add_operation(operations, "copy", old_chunk[0], len(block))
                        reused += len(block)
                    else:
                        add_operation(operations, "data", data.tell(), len(block))
                        data.write(block)
                files.append(dict(identity, chunks=operations))
        manifest = dict(schemaVersion=1, algorithm=ALGORITHM, fromVersion=source_version,
                        version=version, rid=rid, targetSha256=target_sha, files=files)
        raw = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()
        if len(raw) > MAX_MANIFEST or sum(len(f["chunks"]) for f in files) > 262144:
            raise ValueError("Differential manifest exceeds budget")
        with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            info = zipfile.ZipInfo("manifest.json")
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, raw)
            info = zipfile.ZipInfo("data")
            info.compress_type = zipfile.ZIP_DEFLATED
            with archive.open(info, "w", force_zip64=True) as destination, data_path.open("rb") as stream:
                shutil.copyfileobj(stream, destination, 65536)
        verify_bundle(output, source_tree, target_files, manifest)
    return reused


def verify_bundle(bundle, source_tree, expected_files, identity):
    # Independent streaming reconstruction verifies every output, not just chunk matches.
    with tempfile.TemporaryDirectory(prefix="nexa-delta-check-") as temp, zipfile.ZipFile(bundle) as archive:
        if archive.namelist() != ["manifest.json", "data"]:
            raise ValueError("Unexpected differential members")
        with archive.open("manifest.json") as stream:
            raw = stream.read(MAX_MANIFEST + 1)
        if len(raw) > MAX_MANIFEST:
            raise ValueError("Differential manifest exceeds budget")
        manifest = json.loads(raw, object_pairs_hook=unique_object)
        if manifest != identity:
            raise ValueError("Differential identity mismatch")
        literals = Path(temp) / "data"
        with archive.open("data") as input_stream, literals.open("wb") as output:
            copy_exact(input_stream, output, archive.getinfo("data").file_size, MAX_TREE)
        if {f["path"] for f in manifest["files"]} != set(expected_files):
            raise ValueError("Differential target tree mismatch")
        with literals.open("rb") as data:
            for file in manifest["files"]:
                safe_path(file["path"], manifest["rid"])
                if {key: file[key] for key in ("path", "size", "sha256", "executable")} != expected_files[file["path"]]:
                    raise ValueError("Differential file identity mismatch")
                hash_value, actual = hashlib.sha256(), 0
                for kind, offset, size in file["chunks"]:
                    if kind not in ("copy", "data") or offset < 0 or size <= 0 or size > file["size"] - actual:
                        raise ValueError("Invalid differential range")
                    old = (source_tree / file["path"]).open("rb") if kind == "copy" else None
                    stream = old or data
                    try:
                        stream.seek(offset)
                        left = size
                        while left:
                            block = stream.read(min(left, 65536))
                            if not block:
                                raise ValueError("Truncated differential source")
                            hash_value.update(block)
                            actual += len(block)
                            left -= len(block)
                    finally:
                        if old:
                            old.close()
                if actual != file["size"] or hash_value.hexdigest() != file["sha256"]:
                    raise ValueError("Differential reconstruction mismatch")


def generate(directory, version, history):
    if release_channel(version) == "ci":
        return
    patches = []
    for rid in RIDS:
        target = directory / package_name(version, rid)
        with tempfile.TemporaryDirectory(prefix="nexa-delta-tree-") as temp:
            work = Path(temp)
            target_tree = work / "target"
            target_tree.mkdir()
            target_files = unpack(target, target_tree, rid)
            for previous in sorted(history.iterdir()):
                if not previous.is_dir() or canonical_order(previous.name) >= canonical_order(version):
                    continue
                package = previous / package_name(previous.name, rid)
                if not package.is_file():
                    continue
                source_tree = work / previous.name
                source_tree.mkdir()
                unpack(package, source_tree, rid)
                output = directory / delta_name(version, rid, previous.name)
                reused = build_bundle(source_tree, target_tree, target_files, output, version, previous.name, rid, digest(target))
                size = output.stat().st_size
                if not reused or size >= target.stat().st_size or size > MAX_PACKAGE:
                    output.unlink()
                    continue
                patches.append(dict(fromVersion=previous.name, rid=rid, name=output.name, size=size,
                                    sha256=digest(output), targetSha256=digest(target)))
                print(f"Delta {rid} from {previous.name}: {size}/{target.stat().st_size} bytes; reused {reused} bytes")
    if patches:
        index = dict(schemaVersion=1, product="nexacl", version=version, runtimeVariant="nativeaot-self-contained",
                     configuration="Release", patches=patches)
        (directory / INDEX_NAME).write_text(json.dumps(index, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def validate_index(directory, version):
    path = directory / INDEX_NAME
    if not path.exists():
        return set()
    canonical_order(version)
    raw = path.read_bytes()
    if not raw or len(raw) > 1024 * 1024:
        raise ValueError("Differential index budget exceeded")
    index = json.loads(raw, object_pairs_hook=unique_object)
    if set(index) != {"schemaVersion", "product", "version", "runtimeVariant", "configuration", "patches"} or (
            type(index["schemaVersion"]) is not int or index["schemaVersion"] != 1 or index["product"] != "nexacl"
            or index["version"] != version or index["runtimeVariant"] != "nativeaot-self-contained"
            or index["configuration"] != "Release" or not isinstance(index["patches"], list) or not 0 < len(index["patches"]) <= MAX_PATCHES):
        raise ValueError("Invalid differential index identity")
    names, keys = {INDEX_NAME}, set()
    for patch in index["patches"]:
        if set(patch) != {"fromVersion", "rid", "name", "size", "sha256", "targetSha256"}:
            raise ValueError("Invalid differential entry")
        rid, source = patch["rid"], patch["fromVersion"]
        name = delta_name(version, rid, source)
        if rid not in RIDS or canonical_order(source) >= canonical_order(version) or name != patch["name"] or name in names:
            raise ValueError("Invalid differential package identity")
        target, bundle = directory / package_name(version, rid), directory / name
        if type(patch["size"]) is not int or not 0 < patch["size"] < target.stat().st_size or patch["size"] > MAX_PACKAGE:
            raise ValueError("Invalid differential package size")
        if bundle.stat().st_size != patch["size"] or digest(bundle) != patch["sha256"] or digest(target) != patch["targetSha256"]:
            raise ValueError("Differential package digest mismatch")
        names.add(name)
        keys.add((rid, source))
    if len(keys) != len(index["patches"]):
        raise ValueError("Duplicate differential source")
    return names


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("version")
    parser.add_argument("history", type=Path)
    args = parser.parse_args()
    generate(args.directory, args.version, args.history)

