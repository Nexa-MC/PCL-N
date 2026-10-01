import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from metadata import changelog, identity, release_body
from verify import expected_names, verify


class ReleaseTests(unittest.TestCase):
    def test_tag_channels_and_invalid_versions(self):
        sha = "abcdef" + "0" * 34
        for tag, channel in (("2.0.0", "stable"), ("v2.1.0.alpha.12", "alpha"), ("2.0.0.beta.1", "beta"), ("2.0.0.ci.abcdef", "ci")):
            with self.subTest(tag=tag):
                self.assertEqual(channel, identity("refs/tags/" + tag, sha)["channel"])
        for tag in ("2.0.0-beta.1", "2.0.0.alpha.0", "2.0.0.ci.000000", "2.0.0;echo bad", "02.0.0"):
            with self.assertRaises(ValueError):
                identity("refs/tags/" + tag, sha)
        self.assertEqual("false", identity("refs/heads/refactor/xsr", sha)["release"])

    def test_incomplete_or_empty_package_set_cannot_release(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            names = expected_names("2.0.0")
            self.assertEqual(18, len(names))
            for name in names:
                (root / name).write_bytes(b"package")
            verify(root, "2.0.0")
            self.assertEqual(18, len((root / "SHA256SUMS").read_text().splitlines()))
            one = root / next(iter(names))
            one.write_bytes(b"")
            with self.assertRaises(ValueError):
                verify(root, "2.0.0")
            one.unlink()
            with self.assertRaises(ValueError):
                verify(root, "2.0.0")

    def test_changelog_preserves_commit_text_without_executing_it(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            def git(*args):
                return subprocess.check_output(["git", *args], cwd=root, encoding="utf-8").strip()
            git("init", "-q")
            git("config", "gc.auto", "0")
            git("config", "maintenance.auto", "false")
            git("config", "user.name", "Release Test")
            git("config", "user.email", "test@example.invalid")
            git("commit", "--allow-empty", "-m", "old entry")
            git("tag", "2.0.0.alpha.1")
            git("commit", "--allow-empty", "-m", "fix: literal $(touch SHOULD_NOT_EXIST)", "-m", "Details with `backticks` and 中文")
            sha = git("rev-parse", "HEAD")
            environment = dict(os.environ)
            environment.pop("GITHUB_OUTPUT", None)
            subprocess.run([sys.executable, str(Path(__file__).with_name("metadata.py")), "--ref", "refs/tags/v2.0.0.beta.1", "--sha", sha, "--output", "out"], cwd=root, env=environment, check=True)
            notes = (root / "out/CHANGELOG.md").read_text(encoding="utf-8")
            self.assertIn("$(touch SHOULD_NOT_EXIST)", notes)
            self.assertIn("Details with `backticks` and 中文", notes)
            self.assertNotIn("old entry", notes)
            self.assertFalse((root / "SHOULD_NOT_EXIST").exists())


    def test_first_version_changelog_covers_only_the_tagged_commit(self):
        # A bare `git log <sha>` sweeps the entire repository history into the first
        # release; the notes must describe only the tagged commit itself.
        with tempfile.TemporaryDirectory() as work:
            def run(*args, **kwargs):
                return subprocess.check_output(["git", "-C", work, *args], encoding="utf-8").strip()
            run("init", "-q")
            run("config", "gc.auto", "0")
            run("config", "maintenance.auto", "false")
            run("config", "user.email", "test@example.com")
            run("config", "user.name", "Test")
            seed = "seed"
            (Path(work) / "seed.txt").write_text("seed", encoding="utf-8")
            run("add", ".")
            run("commit", "-m", seed)
            run("add", ".")
            (Path(work) / "release.txt").write_text("release", encoding="utf-8")
            run("add", ".")
            run("commit", "-m", "feat: the actual release content")
            # A legacy tag from another product line sharing the repo must not scope this release.
            run("tag", "2.10.5", "HEAD~1")
            run("tag", "v2.0.0.alpha.1")
            sha = run("rev-parse", "HEAD")

            from metadata import changelog
            import metadata
            # changelog() shells out to git in the process CWD; point it at the temp repo.
            original_git = metadata.git
            metadata.git = lambda *args: subprocess.check_output(["git", "-C", work, *args], encoding="utf-8").strip()
            try:
                notes = changelog("2.0.0.alpha.1", None, sha)
            finally:
                metadata.git = original_git
            self.assertIn("the actual release content", notes)
            self.assertNotIn(seed, notes)

            # Mirror metadata.py's previous lookup: only 2.0.0.* tags qualify, and the
            # tagged commit itself is excluded; 2.10.5 must not become `previous`.
            self.assertIsNone(next(
                (tag for tag in run("tag", "--merged", sha).splitlines()
                 if (bare := tag.removeprefix("v")) != "2.0.0" and bare.startswith("2.0.0.")
                 and run("rev-parse", f"{tag}^{{commit}}") != sha),
                None))

    def test_first_version_changelog_scopes_to_the_base_branch(self):
        # The first release of a product line describes the branch's own work — commits
        # since the merge-base with the base branch — not the whole repository history.
        with tempfile.TemporaryDirectory() as work:
            def run(*args, **kwargs):
                return subprocess.check_output(["git", "-C", work, *args], encoding="utf-8").strip()
            run("init", "-q")
            run("config", "gc.auto", "0")
            run("config", "maintenance.auto", "false")
            run("config", "user.email", "test@example.com")
            run("config", "user.name", "Test")
            (Path(work) / "base.txt").write_text("base", encoding="utf-8")
            run("add", ".")
            run("commit", "-m", "chore: base line history")
            run("branch", "dev")
            run("checkout", "-q", "-b", "feature")
            (Path(work) / "one.txt").write_text("one", encoding="utf-8")
            run("add", ".")
            run("commit", "-m", "feat: first branch commit")
            (Path(work) / "two.txt").write_text("two", encoding="utf-8")
            run("add", ".")
            run("commit", "-m", "feat: second branch commit")
            run("tag", "v2.0.0.alpha.1")
            sha = run("rev-parse", "HEAD")

            import metadata
            original_git = metadata.git
            metadata.git = lambda *args: subprocess.check_output(["git", "-C", work, *args], encoding="utf-8").strip()
            try:
                notes = metadata.changelog("2.0.0.alpha.1", None, sha, "dev")
                fallback = metadata.changelog("2.0.0.alpha.1", None, sha, "missing-ref")
            finally:
                metadata.git = original_git
            self.assertIn("first branch commit", notes)
            self.assertIn("second branch commit", notes)
            self.assertNotIn("base line history", notes)
            # An unresolvable base falls back to describing only the tagged commit.
            self.assertNotIn("first branch commit", fallback)
            self.assertIn("second branch commit", fallback)

    def test_release_body_lists_every_package_with_purpose(self):
        from metadata import release_body
        body = release_body({"version": "2.0.0.alpha.1", "channel": "alpha"}, "# 更新内容\n\n- fix", "v2.0.0.alpha.0")
        self.assertIn("setup.exe", body)
        self.assertIn("portable.zip", body)
        self.assertIn("AppImage", body)
        self.assertIn("dmg", body)
        self.assertIn("SHA256SUMS", body)
        self.assertIn("更新内容", body)




if __name__ == "__main__":
    unittest.main()
