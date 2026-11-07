import unittest
from unittest.mock import Mock, patch

import finish_release as release
import prepare_release

HEAD, BASE, MERGED = "b" * 40, "a" * 40, "c" * 40
TARGET = "0.0.2"


def pr():
    return {"head": {"sha": HEAD, "ref": "prepare-release/v" + TARGET, "repo": {"full_name": "owner/repo"}},
            "base": {"sha": BASE, "ref": "main", "repo": {"full_name": "owner/repo"}},
            "draft": False, "state": "open", "merged": False}


def ci():
    run = {"event": "pull_request", "head_sha": HEAD, "conclusion": "success", "pull_requests": [{"number": 4}]}
    jobs = [{"name": "Windows tests and checks", "conclusion": "success", "steps": [
        {"name": f"Release CI identity: PR 4 head {HEAD} base {BASE}", "conclusion": "success"}]}]
    return run, jobs


@patch.dict("os.environ", {"GITHUB_REPOSITORY": "owner/repo"})
class FinishReleaseTests(unittest.TestCase):
    def setUp(self):
        self.pull = pr()
        self.old = "<Version>0.0.1</Version>\n<Version>0.0.1-dev</Version>"
        self.new = self.old.replace("0.0.1", TARGET)
        self.tag = ""
        self.main = BASE
        self.tree = "checked-tree"
        self.calls = []

    def git(self, *args):
        if args[0] in ("fetch", "merge-base"):
            return ""
        if args[0] == "rev-parse":
            return HEAD
        if args[0] == "diff":
            return "Directory.Build.props\nrelease-notes/v0.0.2.md"
        if args[0] == "ls-remote":
            return self.tag
        if args[:3] == ("show", "-s", "--format=%P"):
            return BASE
        if args[:3] == ("show", "-s", "--format=%T"):
            return "checked-tree" if args[3] == HEAD else self.tree
        if args == ("show", BASE + ":Directory.Build.props"):
            return self.old
        if args == ("show", HEAD + ":Directory.Build.props"):
            return self.new
        if args == ("show", HEAD + ":release-notes/v0.0.2.md"):
            return "# v0.0.2\n\n## English\n\n- Change\n\n## Русский\n\n- Изменение"
        raise AssertionError(args)

    def api(self, path, method="GET", **fields):
        self.calls.append((path, method, fields))
        if path == "pulls/4":
            return dict(self.pull)
        if path == "git/ref/heads/main":
            return {"object": {"sha": self.main}}
        if path == "pulls/4/merge":
            self.pull.update(merged=True, merge_commit_sha=MERGED)
            return {"merged": True}
        if path == "git/refs":
            return {}
        raise AssertionError(path)

    def finish(self, wait=None):
        with patch.object(release, "git", side_effect=self.git), patch.object(release, "api", side_effect=self.api), \
             patch.object(release, "wait_ci", side_effect=wait) as checked:
            release.finish(TARGET, 4, HEAD)
            checked.assert_called_once_with(4, TARGET, HEAD, BASE)

    def test_success_squashes_exact_head_and_tags_merged_commit(self):
        self.finish()
        self.assertIn(("pulls/4/merge", "PUT", {"sha": HEAD, "merge_method": "squash"}), self.calls)
        self.assertIn(("git/refs", "POST", {"ref": "refs/tags/v0.0.2", "sha": MERGED}), self.calls)

    def test_ci_failure_prevents_merge_and_tag(self):
        with self.assertRaisesRegex(RuntimeError, "CI failed"):
            self.finish(RuntimeError("CI failed"))
        self.assertFalse(any(method != "GET" for _, method, _ in self.calls))

    def test_changed_pr_or_main_prevents_writes(self):
        for changed in ("head", "main"):
            self.setUp()
            if changed == "head":
                self.pull["head"]["sha"] = "d" * 40
            else:
                self.main = "d" * 40
            with self.assertRaises(RuntimeError):
                self.finish()
            self.assertFalse(any(method != "GET" for _, method, _ in self.calls))

    def test_merge_tree_must_match_checked_tree_before_tagging(self):
        self.tree = "unexpected-tree"
        with self.assertRaisesRegex(RuntimeError, "tree differs"):
            self.finish()
        self.assertFalse(any(path == "git/refs" for path, _, _ in self.calls))

    def test_retry_of_merged_pr_with_matching_tag_changes_nothing(self):
        self.pull.update(merged=True, merge_commit_sha=MERGED)
        self.tag = MERGED + "\trefs/tags/v0.0.2"
        self.finish()
        self.assertFalse(any(method != "GET" for _, method, _ in self.calls))

    def test_existing_tag_is_never_moved(self):
        self.pull.update(merged=True, merge_commit_sha=MERGED)
        self.tag = "d" * 40 + "\trefs/tags/v0.0.2"
        with self.assertRaisesRegex(RuntimeError, "will not be moved"):
            self.finish()

    def test_extra_version_file_changes_are_rejected(self):
        self.new += "\n<UnexpectedProperty>true</UnexpectedProperty>"
        with self.assertRaisesRegex(RuntimeError, "more than version"):
            self.finish()

    def test_full_ci_rejects_skips_wrong_base_and_other_pr(self):
        for changed in ("skip", "base", "pr"):
            run, jobs = ci()
            if changed == "skip":
                jobs[0]["conclusion"] = "skipped"
            elif changed == "base":
                jobs[0]["steps"] = []
            else:
                run["pull_requests"] = [{"number": 5}]
            with self.assertRaises(RuntimeError):
                release.full_ci(run, jobs, 4, HEAD, BASE)

    def test_successful_ci_identity_remains_checkable_after_merge(self):
        run, jobs = ci()
        run["pull_requests"] = []
        release.full_ci(run, jobs, 4, HEAD, BASE)

    def test_existing_release_pr_is_resumed_without_regenerating_notes(self):
        with patch.dict("os.environ", {"GH_TOKEN": "synthetic-token", "RELEASE_VERSION": TARGET}), \
             patch.object(prepare_release, "version", return_value="0.0.1"), \
             patch.object(prepare_release, "existing_release_pr", return_value=4), \
             patch.object(prepare_release, "generate_notes") as generate, patch.object(prepare_release.sp, "run") as run:
            prepare_release.main()
            generate.assert_not_called()
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
