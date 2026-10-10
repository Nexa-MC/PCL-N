import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import release_notes


class ReleaseNotesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.notes = self.root / "notes.md"
        self.notes.write_text("# 手写更新\n\n- literal $(touch NEVER) and `backticks`\n", encoding="utf-8")
        self.notes.with_suffix(".notes.json").write_text(json.dumps({
            "repository": "owner/repo", "tag": "v2.0.0.alpha.6", "allow_create": True,
        }), encoding="utf-8")
        self.assets = self.root / "artifacts"
        self.assets.mkdir()
        (self.assets / "package.zip").write_bytes(b"fixture")

    def publish(self, channel="alpha"):
        release_notes.publish("owner/repo", "v2.0.0.alpha.6", "2.0.0.alpha.6", channel, self.notes, self.assets)

    def test_existing_body_overrides_dispatch_text(self):
        body = "# 维护者的新正文\n\n- edited while preparing\n"
        with patch.object(release_notes, "get_release", return_value={"body": body}):
            release_notes.prepare("owner/repo", "tag", self.notes, "stale dispatch text")
        self.assertEqual(body, self.notes.read_text(encoding="utf-8"))

    def test_manual_input_supplies_only_missing_release(self):
        manual = "  # 中文\n\n- 多行 `text` $(no execution)\n"
        with patch.object(release_notes, "get_release", return_value=None):
            release_notes.prepare("owner/repo", "tag", self.notes, manual)
        self.assertEqual(manual, self.notes.read_text(encoding="utf-8"))
        for existing in ({"body": ""}, {"body": None}, {"body": " \n "}):
            with self.subTest(existing=existing), patch.object(release_notes, "get_release", return_value=existing):
                with self.assertRaises(ValueError):
                    release_notes.prepare("owner/repo", "tag", self.notes, manual)

    def test_missing_or_whitespace_notes_fail_without_creating_output(self):
        output = self.root / "not-created.md"
        for existing in (None, {"body": "\n "}):
            with self.subTest(existing=existing), patch.object(release_notes, "get_release", return_value=existing):
                with self.assertRaisesRegex(ValueError, "non-empty"):
                    release_notes.prepare("owner/repo", "tag", output, " \n")
                self.assertFalse(output.exists())

    def test_publish_preserves_newer_nonempty_body_and_only_uploads(self):
        with patch.object(release_notes, "get_release", return_value={"body": "newer manual edits"}), \
                patch.object(release_notes.subprocess, "run") as run:
            self.publish()
        self.assertEqual(1, run.call_count)
        self.assertEqual(["gh", "release", "upload"], run.call_args.args[0][:3])
        self.assertNotIn("--notes-file", run.call_args.args[0])

    def test_body_cleared_during_build_is_never_restored_or_published(self):
        with patch.object(release_notes, "get_release", side_effect=[{"body": "before build"}, {"body": " "}]), \
                patch.object(release_notes.subprocess, "run") as run:
            release_notes.prepare("owner/repo", "v2.0.0.alpha.6", self.notes)
            with self.assertRaises(ValueError):
                self.publish()
        run.assert_not_called()

    def test_new_release_requires_manual_notes_and_preserves_channel(self):
        for channel in ("alpha", "stable"):
            with self.subTest(channel=channel), patch.object(release_notes, "get_release", return_value=None), \
                    patch.object(release_notes.subprocess, "run") as run:
                self.publish(channel)
                command = run.call_args.args[0]
                self.assertEqual(["gh", "release", "create"], command[:3])
                self.assertIn("--verify-tag", command)
                self.assertIn(str(self.notes), command)
                self.assertIn(str(self.assets / "package.zip"), command)
                self.assertEqual(channel != "stable", "--prerelease" in command)

    def test_existing_release_deleted_during_build_is_never_recreated(self):
        with patch.object(release_notes, "get_release", side_effect=[{"body": "draft notes", "draft": True}, None]), \
                patch.object(release_notes.subprocess, "run") as run:
            release_notes.prepare("owner/repo", "v2.0.0.alpha.6", self.notes, "must not authorize replacement")
            with self.assertRaisesRegex(ValueError, "removed"):
                self.publish()
        run.assert_not_called()

    def test_provenance_must_match_target_before_any_api_call(self):
        self.notes.with_suffix(".notes.json").write_text(json.dumps({
            "repository": "other/repo", "tag": "v2.0.0.alpha.6", "allow_create": True,
        }), encoding="utf-8")
        with patch.object(release_notes.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "source"):
                self.publish()
        run.assert_not_called()

    def test_publish_rejects_empty_notes_before_read_or_mutation(self):
        self.notes.write_text(" \n", encoding="utf-8")
        with patch.object(release_notes, "get_release") as get, patch.object(release_notes.subprocess, "run") as run:
            with self.assertRaises(ValueError):
                self.publish()
        get.assert_not_called()
        run.assert_not_called()

    def test_publish_rejects_empty_assets_before_mutation(self):
        (self.assets / "package.zip").unlink()
        with patch.object(release_notes.subprocess, "run") as run:
            with self.assertRaisesRegex(ValueError, "assets"):
                self.publish()
        run.assert_not_called()

    def response(self, release, **extra):
        return {"data": {"repository": {"nameWithOwner": "owner/repo", "release": release}}, **extra}

    def test_release_read_finds_published_and_draft_with_literal_tag(self):
        for draft in (True, False):
            payload = self.response({"tagName": "tag/name", "description": "manual", "isDraft": draft})
            result = subprocess.CompletedProcess([], 0, json.dumps(payload), "")
            with self.subTest(draft=draft), patch.object(release_notes.subprocess, "run", return_value=result) as run:
                self.assertEqual({"body": "manual", "draft": draft}, release_notes.get_release("owner/repo", "tag/name"))
            self.assertIn("tagName=tag/name", run.call_args.args[0])
            self.assertIn("graphql", run.call_args.args[0])

    def test_only_confirmed_null_release_means_missing(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(self.response(None)), "")
        with patch.object(release_notes.subprocess, "run", return_value=result):
            self.assertIsNone(release_notes.get_release("owner/repo", "tag"))
        for result in (subprocess.CompletedProcess([], 1, "", "network failed"),
                       subprocess.CompletedProcess([], 1, '{"message":"Forbidden"}', "gh: (HTTP 403)")):
            with self.subTest(result=result), patch.object(release_notes.subprocess, "run", return_value=result):
                with self.assertRaises(RuntimeError):
                    release_notes.get_release("owner/repo", "tag")

    def test_invalid_or_partial_graphql_success_is_rejected(self):
        malformed = [[], {}, {"data": {"repository": None}},
                     self.response(None, errors=[{"message": "permission denied"}]),
                     self.response({"tagName": "wrong", "description": "text", "isDraft": False}),
                     self.response({"tagName": "tag", "description": 123, "isDraft": False}),
                     self.response({"tagName": "tag", "isDraft": False})]
        for payload in malformed:
            result = subprocess.CompletedProcess([], 0, json.dumps(payload), "")
            with self.subTest(payload=payload), patch.object(release_notes.subprocess, "run", return_value=result):
                with self.assertRaises(ValueError):
                    release_notes.get_release("owner/repo", "tag")

    def test_metadata_requires_notes_for_tag_without_git_log_fallback(self):
        import metadata
        argv = ["metadata.py", "--ref", "refs/tags/v2.0.0.alpha.6", "--sha", "a" * 40,
                "--output", str(self.root / "out")]
        with patch.object(sys, "argv", argv), patch.object(metadata, "git", return_value="a" * 40) as git:
            with self.assertRaises(SystemExit):
                metadata.main()
            self.assertEqual(1, git.call_count)
        self.assertFalse((self.root / "out").exists())

    def test_metadata_rejects_whitespace_file_and_branch_does_not_log_commits(self):
        import metadata
        self.notes.write_text(" \n", encoding="utf-8")
        argv = ["metadata.py", "--sha", "a" * 40, "--output", str(self.root / "out"),
                "--release-notes-file", str(self.notes)]
        with patch.object(metadata, "git", return_value="a" * 40) as git:
            with patch.object(sys, "argv", [*argv, "--ref", "refs/tags/v2.0.0.alpha.6"]):
                with self.assertRaises(ValueError):
                    metadata.main()
            with patch.object(sys, "argv", [*argv, "--ref", "refs/heads/refactor/xsr"]):
                metadata.main()
            self.assertEqual(2, git.call_count)
        self.assertTrue((self.root / "out/RELEASE.md").read_text().startswith("CI build only."))


if __name__ == "__main__":
    unittest.main()
