"""Fetch bounded, publisher-authenticated historical packages from the fixed repository."""
import argparse
import json
import os
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

from delta import MAX_PACKAGE, RIDS, canonical_order, digest, package_name
from manifest import release_channel, unique_object
from sign import FINGERPRINT, gpg, import_public, keyring
from verify import expected_names

REPOSITORY = "PCL-N-Edition/PCL-N"
ROOT = Path(__file__).resolve().parents[2]
HISTORY_LIST_LIMIT = 2  # At most the current release and one preceding published record.
HISTORY_LIST_BYTES = 4 * 1024 * 1024


def download(url, output, maximum, authenticated=False):
    headers = {"User-Agent": "NexaCL-Release/1.0"}
    token = os.environ.get("GH_TOKEN", "")
    if authenticated and token:
        headers["Authorization"] = "Bearer " + token
    # No credentials accompany release asset redirects.
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120) as response, output.open("xb") as stream:
        actual = 0
        while block := response.read(128 * 1024):
            actual += len(block)
            if actual > maximum:
                raise ValueError("Historical response exceeded byte budget")
            stream.write(block)


def validate_remote_manifest(raw, version):
    manifest = json.loads(raw, object_pairs_hook=unique_object)
    if set(manifest) != {"schemaVersion", "product", "version", "channel", "runtimeVariant", "configuration", "assets"} or (
            type(manifest["schemaVersion"]) is not int or manifest["schemaVersion"] != 1
            or manifest["product"] != "nexacl" or manifest["version"] != version
            or manifest["channel"] != release_channel(version) or manifest["runtimeVariant"] != "nativeaot-self-contained"
            or manifest["configuration"] != "Release" or not isinstance(manifest["assets"], list) or len(manifest["assets"]) != 18):
        raise ValueError("Historical signed envelope identity mismatch")
    expected, assets = expected_names(version), {}
    for asset in manifest["assets"]:
        if not isinstance(asset, dict) or set(asset) != {"name", "rid", "format", "size", "sha256"}:
            raise ValueError("Invalid historical asset")
        name = asset["name"]
        if name not in expected or name in assets or name != f'Nexa-{version}-{asset["rid"]}.{asset["format"]}' or (
                type(asset["size"]) is not int or not 0 < asset["size"] <= MAX_PACKAGE
                or not isinstance(asset["sha256"], str) or len(asset["sha256"]) != 64
                or any(c not in "0123456789abcdef" for c in asset["sha256"])):
            raise ValueError("Invalid historical asset identity")
        assets[name] = asset
    if set(assets) != expected:
        raise ValueError("Incomplete historical envelope")
    return assets


def latest_history_candidate(raw, version):
    """Select only the first other published record, never an older fallback."""
    releases = json.loads(raw, object_pairs_hook=unique_object)
    if not isinstance(releases, list) or len(releases) > HISTORY_LIST_LIMIT:
        raise ValueError("Invalid bounded historical release list")
    seen_ids, seen_versions = set(), set()
    for release in releases:
        if (not isinstance(release, dict) or type(release.get("id")) is not int or release["id"] <= 0
                or type(release.get("draft")) is not bool or not isinstance(release.get("tag_name"), str)
                or not release["tag_name"] or len(release["tag_name"]) > 128):
            raise ValueError("Invalid historical release record")
        value = release["tag_name"].removeprefix("v")
        if release["id"] in seen_ids or value in seen_versions:
            raise ValueError("Duplicate historical release record")
        seen_ids.add(release["id"])
        seen_versions.add(value)
    for release in releases:
        tag = release["tag_name"]
        value = tag.removeprefix("v")
        if release["draft"] or value == version:
            continue
        try:
            eligible = release_channel(value) == release_channel(version) and canonical_order(value) < canonical_order(version)
        except ValueError:
            eligible = False
        # The latest other published release owns the one-history slot, even if it
        # is incompatible. Do not search the second record for a more useful base.
        return (value, tag) if eligible else None
    return None


def fetch(history, version):
    history.mkdir(parents=True, exist_ok=True)
    if release_channel(version) == "ci":
        return
    with tempfile.TemporaryDirectory(prefix="nexa-history-") as temp, keyring() as home:
        work = Path(temp)
        import_public(home, (ROOT / "GPG-PUBLIC-KEY.asc").read_bytes(), FINGERPRINT)
        listing = work / "releases"
        download(f"https://api.github.com/repos/{REPOSITORY}/releases?per_page={HISTORY_LIST_LIMIT}&page=1",
                 listing, HISTORY_LIST_BYTES, authenticated=True)
        candidate = latest_history_candidate(listing.read_bytes(), version)
        accepted = 0
        for value, tag in [candidate] if candidate is not None else []:
            directory = work / value
            directory.mkdir()
            base = f"https://github.com/{REPOSITORY}/releases/download/{tag}/"
            manifest = directory / "Nexa-Release.json"
            try:
                download(base + manifest.name, manifest, 1024 * 1024)
            except urllib.error.HTTPError as error:
                if error.code == 404:
                    continue  # Older releases without the authenticated XSR envelope are not sources.
                raise
            signature = directory / "Nexa-Release.json.asc"
            download(base + signature.name, signature, 1024 * 1024)
            status = gpg(home, "--status-fd", "1", "--verify", str(signature), str(manifest))
            signers = [line.split()[2].decode() for line in status.splitlines() if line.startswith(b"[GNUPG:] VALIDSIG ")]
            if signers != [FINGERPRINT]:
                raise ValueError("Historical release signer mismatch")
            assets = validate_remote_manifest(manifest.read_bytes(), value)
            destination = history / value
            destination.mkdir()
            for rid in RIDS:
                name = package_name(value, rid)
                path = destination / name
                download(base + name, path, assets[name]["size"])
                if path.stat().st_size != assets[name]["size"] or digest(path) != assets[name]["sha256"]:
                    raise ValueError("Historical package did not match authenticated bytes")
            accepted += 1
            print(f"Authenticated historical source {value} for six platforms")
        if not accepted:
            print("The latest historical release has no compatible authenticated source; publishing full packages only.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("history", type=Path)
    parser.add_argument("version")
    args = parser.parse_args()
    fetch(args.history, args.version)

