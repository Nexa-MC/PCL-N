"""The signed release identity contract, independent of download routing."""
import hashlib
import json

from metadata import TAG
from verify import FORMATS

MANIFEST_NAME = "Nexa-Release.json"
MAXIMUM_MANIFEST_BYTES = 1024 * 1024


def release_channel(version):
    match = TAG.fullmatch(version)
    if not match or version.startswith("v") or len(version) > 128:
        raise ValueError("Manifest version must use the canonical dotted XSR form")
    _, _, _, _, channel, _, commit = match.groups()
    if any(int(part) > 9223372036854775807 for part in version.split(".") if part.isascii() and part.isdecimal()):
        raise ValueError("Manifest version exceeds the runtime version contract")
    return channel or ("ci" if commit else "stable")


def build_manifest(directory, version):
    channel = release_channel(version)
    assets = []
    for platform, formats in FORMATS.items():
        for arch in ("x64", "arm64"):
            for format_name in formats:
                rid = f"{platform}-{arch}"
                name = f"Nexa-{version}-{rid}.{format_name}"
                digest = hashlib.sha256()
                length = 0
                with (directory / name).open("rb") as stream:
                    while chunk := stream.read(128 * 1024):
                        digest.update(chunk)
                        length += len(chunk)
                if length <= 0:
                    raise ValueError(f"Empty package: {name}")
                assets.append(dict(name=name, rid=rid, format=format_name,
                                   size=length, sha256=digest.hexdigest()))
    return dict(schemaVersion=1, product="nexacl", version=version, channel=channel,
                runtimeVariant="nativeaot-self-contained", configuration="Release",
                assets=sorted(assets, key=lambda asset: asset["name"]))


def write_manifest(directory, version):
    data = build_manifest(directory, version)
    raw = (json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    if len(raw) > MAXIMUM_MANIFEST_BYTES:
        raise ValueError("Release manifest exceeds its byte budget")
    (directory / MANIFEST_NAME).write_bytes(raw)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate release manifest property")
        result[key] = value
    return result


def validate_manifest(directory, version):
    with (directory / MANIFEST_NAME).open("rb") as stream:
        raw = stream.read(MAXIMUM_MANIFEST_BYTES + 1)
    if not raw or len(raw) > MAXIMUM_MANIFEST_BYTES:
        raise ValueError("Release manifest exceeds its byte budget")
    data = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    if not isinstance(data, dict) or type(data.get("schemaVersion")) is not int:
        raise ValueError("Invalid release manifest schema")
    assets = data.get("assets")
    if not isinstance(assets, list) or len(assets) != 18 or any(
            not isinstance(asset, dict) or type(asset.get("size")) is not int for asset in assets):
        raise ValueError("Invalid release manifest assets")
    expected = build_manifest(directory, version)
    # Order is not authority; names are unique and all eighteen identities must agree.
    names = [asset.get("name") for asset in assets]
    if any(not isinstance(name, str) for name in names) or len(set(names)) != len(names):
        raise ValueError("Duplicate or invalid release asset name")
    data["assets"] = sorted(assets, key=lambda asset: asset["name"])
    if data != expected:
        raise ValueError("Release manifest identity or package bytes do not match")
