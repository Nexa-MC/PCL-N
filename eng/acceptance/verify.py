"""Reviewed Minecraft launch evidence admission; never manufacture launch evidence."""
import argparse
import hashlib
import json
import math
import re
from pathlib import Path

POLICY = Path(__file__).with_name("minecraft-matrix.json")
PLATFORMS = {"win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"}
LOADERS = {"Vanilla", "Forge", "NeoForge", "Cleanroom", "Fabric", "Legacy Fabric", "Quilt", "LiteLoader", "OptiFine", "LabyMod"}
MAX_JSON = 1024 * 1024
MAX_ARTIFACT = 64 * 1024 * 1024


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def read_json(path):
    require(path.is_file() and not path.is_symlink(), "record must be a regular file")
    with path.open("rb") as stream:
        data = stream.read(MAX_JSON + 1)
    require(len(data) <= MAX_JSON, "record exceeds budget")
    return json.loads(data, object_pairs_hook=unique_object,
                      parse_constant=lambda value: require(False, f"invalid constant: {value}"))


def number(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def load_policy(path=POLICY):
    policy = read_json(path)
    require(policy["schema"] == 1 and policy["minimum_world_seconds"] >= 600, "invalid policy")
    seen = set()
    for case in policy["cases"]:
        require(re.fullmatch(r"[a-z0-9-]{1,100}", case["id"]), "invalid case id")
        identity = tuple(case[k] for k in ("minecraft", "loader", "java", "platform"))
        require(case["id"] not in seen and identity not in seen, "duplicate matrix case")
        seen.update((case["id"], identity))
        require(case["platform"] in PLATFORMS and case["loader"] in LOADERS
                and type(case["java"]) is int and case["java"] in (8, 17, 21, 25), "invalid target")
        require(re.fullmatch(r"1\.\d+\.\d+", case["minecraft"]), "pin an exact Minecraft version")
        require(case["status"] == "candidate", "policy targets are not support declarations")
    require(policy["cases"], "empty policy")
    return policy


def artifact(root, item):
    relative = item["path"]
    require(isinstance(relative, str) and re.fullmatch(r"[A-Za-z0-9_.\-/]{1,200}", relative)
            and not relative.startswith("/") and all(p not in ("", ".", "..") for p in relative.split("/")), "invalid artifact path")
    path = root
    for part in relative.split("/"):
        path = path / part
        require(not path.is_symlink(), "linked artifact")
    require(path.is_file() and path.stat().st_size <= MAX_ARTIFACT, "artifact missing or over budget")
    expected = item["sha256"]
    require(isinstance(expected, str) and re.fullmatch(r"[0-9a-f]{64}", expected), "invalid artifact hash")
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as stream:
        while chunk := stream.read(65536):
            length += len(chunk)
            require(length <= MAX_ARTIFACT, "artifact grew beyond budget")
            digest.update(chunk)
    require(length > 0 and digest.hexdigest() == expected, "artifact hash mismatch")


def verify_record(record, case, commit, root, minimum):
    require(record["schema"] == 1 and record["case_id"] == case["id"], "wrong evidence schema or case")
    require(record["commit"] == commit, "evidence belongs to another commit")
    review = record["review"]
    require(review["approved"] is True and re.fullmatch(r"[A-Za-z0-9_-]{1,64}", review["reviewer"]), "review required")
    if record["result"] == "not-applicable":
        require(isinstance(record["reason"], str) and 20 <= len(record["reason"]) <= 1000, "exclusion needs a reviewed reason")
        return "not-applicable"
    require(record["result"] == "passed", "launch did not pass")
    require(record["kind"] == "real-minecraft-world" and record["environment"] == "physical-desktop", "smoke/fixture/container is not real desktop evidence")
    require(record["native_host"] is True and record["authenticated_session"] is True, "native authenticated Host required")
    require(record["forced_stop"] is False and record["exit_code"] == 0 and type(record["exit_code"]) is int, "normal successful exit required")
    for key in ("minecraft", "loader", "java", "platform"):
        require(type(record[key]) is type(case[key]) and record[key] == case[key], f"wrong target: {key}")
    require(isinstance(record["loader_version"], str) and 0 < len(record["loader_version"]) <= 100, "loader identity required")
    phases = record["phases_seconds"]
    times = [phases[k] for k in ("host-connected", "client-ready", "world-entered", "world-left", "normal-exit")]
    require(all(number(v) for v in times) and all(a <= b for a, b in zip(times, times[1:])), "invalid phase order")
    require(times[3] - times[2] >= minimum, "world run is too short")
    artifacts = record["artifacts"]
    require(isinstance(artifacts, list) and 2 <= len(artifacts) <= 8, "bounded visual and observation artifacts required")
    require({a["kind"] for a in artifacts} >= {"world-visual", "host-observation"}, "world visual and Host observation required")
    require(len({a["path"] for a in artifacts}) == len(artifacts), "duplicate artifacts")
    for item in artifacts:
        artifact(root, item)
    return "verified"


def coverage(policy, directory, commit):
    require(re.fullmatch(r"[0-9a-f]{40}", commit), "pin a full Git commit SHA")
    require(directory.is_dir() and not directory.is_symlink(), "evidence directory required")
    cases = {case["id"]: case for case in policy["cases"]}
    results = {identity: "pending" for identity in cases}
    records = sorted(directory.glob("*.json"))
    require(len(records) <= len(cases), "too many evidence records")
    for path in records:
        record = read_json(path)
        identity = record["case_id"]
        require(identity in cases and results[identity] == "pending", "unknown or duplicate case evidence")
        results[identity] = verify_record(record, cases[identity], commit, directory, policy["minimum_world_seconds"])
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--policy-only", action="store_true")
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--commit")
    parser.add_argument("--require-all", action="store_true")
    args = parser.parse_args()
    try:
        policy = load_policy()
        if args.policy_only:
            require(not (args.evidence or args.commit or args.require_all), "policy-only is not an evidence gate")
            print(f"Policy valid: {len(policy['cases'])} candidates; no compatibility claim.")
            return 0
        require(args.evidence is not None and args.commit is not None, "--evidence and --commit required")
        results = coverage(policy, args.evidence, args.commit)
        print(json.dumps({"schema": 1, "commit": args.commit, "cases": results,
                          "complete": "pending" not in results.values()}, indent=2))
        return 1 if args.require_all and "pending" in results.values() else 0
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"Evidence rejected: {error}")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
