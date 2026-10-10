"""Use human-written GitHub Release bodies; never synthesize release notes from Git."""
import argparse
import json
import os
import subprocess
from pathlib import Path


def require_notes(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("Write non-empty release notes in GitHub Release before publishing; automatic Git logs are disabled")
    return value


def get_release(repository, tag):
    owner, name = repository.split("/", 1)
    # GraphQL finds both published releases and accessible drafts by pending tag.
    # REST /releases/tags alone cannot find a prewritten draft.
    query = """query($owner: String!, $name: String!, $tagName: String!) {
      repository(owner: $owner, name: $name) {
        nameWithOwner
        release(tagName: $tagName) { tagName description isDraft }
      }
    }"""
    result = subprocess.run(
        ["gh", "api", "graphql", "-f", "query=" + query, "-f", "owner=" + owner,
         "-f", "name=" + name, "-f", "tagName=" + tag],
        capture_output=True, text=True, check=False)
    if result.returncode:
        raise RuntimeError("Could not read GitHub Release; refusing to infer that it is missing")
    payload = json.loads(result.stdout)
    if not isinstance(payload, dict) or payload.get("errors"):
        raise ValueError("Invalid or incomplete GitHub Release response")
    data = payload.get("data")
    repo = data.get("repository") if isinstance(data, dict) else None
    if (not isinstance(repo, dict) or not isinstance(repo.get("nameWithOwner"), str)
            or repo["nameWithOwner"].lower() != repository.lower()
            or "release" not in repo):
        raise ValueError("Could not read the expected GitHub repository")
    release = repo["release"]
    if release is None:
        return None
    if (not isinstance(release, dict) or release.get("tagName") != tag
            or "description" not in release or not isinstance(release["description"], (str, type(None)))
            or type(release.get("isDraft")) is not bool):
        raise ValueError("Invalid GitHub Release response")
    return {"body": release["description"], "draft": release["isDraft"]}


def prepare(repository, tag, output, manual_notes=""):
    release = get_release(repository, tag)
    existing = (release or {}).get("body") or ""
    # A maintained Release body is authoritative, even if dispatch supplied stale text.
    notes = require_notes(existing if release is not None else manual_notes)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(notes, encoding="utf-8")
    output.with_suffix(".notes.json").write_text(json.dumps({
        "repository": repository, "tag": tag, "allow_create": release is None,
    }), encoding="utf-8")


def publish(repository, tag, version, channel, notes_file, assets_directory):
    require_notes(notes_file.read_text(encoding="utf-8"))
    source = json.loads(notes_file.with_suffix(".notes.json").read_text(encoding="utf-8"))
    if (not isinstance(source, dict) or source.get("repository") != repository
            or source.get("tag") != tag or type(source.get("allow_create")) is not bool):
        raise ValueError("Release notes source does not match this publication")
    assets = sorted(str(path) for path in assets_directory.iterdir() if path.is_file())
    if not assets:
        raise ValueError("Refusing to publish a release without distribution assets")
    release = get_release(repository, tag)
    common = ["--repo", repository]
    if release is not None:
        # Re-read after the build. Never overwrite notes edited while CI was running.
        require_notes(release.get("body"))
        subprocess.run(["gh", "release", "upload", tag, *common, *assets, "--clobber"], check=True)
    else:
        if not source["allow_create"]:
            raise ValueError("The prepared Release was removed; refusing to recreate or publish it")
        flags = [] if channel == "stable" else ["--prerelease", "--latest=false"]
        subprocess.run(["gh", "release", "create", tag, *common, *assets, "--verify-tag",
                        "--title", f"Nexa {version}", "--notes-file", str(notes_file), *flags], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("prepare", "publish"):
        command = commands.add_parser(name)
        command.add_argument("--repository", required=True)
        command.add_argument("--tag", required=True)
        if name == "prepare":
            command.add_argument("--output", type=Path, required=True)
        else:
            command.add_argument("--version", required=True)
            command.add_argument("--channel", required=True)
            command.add_argument("--notes-file", type=Path, required=True)
            command.add_argument("--assets-directory", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.repository, args.tag, args.output, os.environ.get("MANUAL_RELEASE_NOTES", ""))
    else:
        publish(args.repository, args.tag, args.version, args.channel, args.notes_file, args.assets_directory)


if __name__ == "__main__":
    main()
