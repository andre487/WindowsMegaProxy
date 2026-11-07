import io
import json
import unittest
import urllib.error
from unittest.mock import Mock, patch

import release_notes as notes


def published(tag, date="2026-11-01T12:00:00Z"):
    return {"tag_name": tag, "published_at": date, "draft": False, "prerelease": False,
            "assets": [{"name": name, "size": 100, "state": "uploaded"}
                       for name in set(notes.package_names(tag).values()) | notes.METADATA | notes.REPORTS]}


class ReleaseNotesTests(unittest.TestCase):
    def test_no_releases_is_the_first_release(self):
        with patch.object(notes, "gh", return_value="[[]]"), patch.object(notes.sp, "run") as run:
            self.assertIsNone(notes.previous_release("0.0.1"))
            run.assert_not_called()

    def test_skips_empty_partial_draft_preview_and_newer_releases_across_pages(self):
        empty = published("v0.0.5"); empty["assets"] = []
        partial = published("v0.0.4"); partial["assets"][0]["size"] = 0
        draft = published("v0.0.3"); draft["draft"] = True
        preview = published("v0.0.2"); preview["prerelease"] = True
        with patch.object(notes, "gh", return_value=json.dumps([[empty, partial, draft, preview, published("v0.0.6")], [published("v0.0.1")]])), patch.object(notes.sp, "run", return_value=Mock(returncode=0)):
            self.assertEqual(notes.previous_release("0.0.6"), "v0.0.1")

    def test_latest_successful_publication_is_used_and_non_ancestors_are_skipped(self):
        releases = [published("v0.0.1"), published("v0.0.2", "2026-11-02T12:00:00Z")]
        with patch.object(notes, "gh", return_value=json.dumps([releases])), patch.object(notes.sp, "run", side_effect=[Mock(returncode=1), Mock(returncode=0)]):
            self.assertEqual(notes.previous_release("0.0.3"), "v0.0.1")

    def test_first_release_uses_empty_tree_not_a_missing_tag(self):
        with patch.object(notes, "previous_release", return_value=None), patch.object(notes.sp, "check_output", side_effect=["initial history", "empty-tree", "full diff"]) as git:
            result = json.loads(notes.evidence("0.0.1"))
            self.assertIsNone(result["previous_tag"])
            self.assertEqual(result["diff"], "full diff")
            self.assertIn("empty-tree", git.call_args.args[0])

    def test_diff_starts_at_the_successful_release(self):
        with patch.object(notes, "previous_release", return_value="v0.0.1"), patch.object(notes.sp, "check_output", side_effect=["history", "net diff"]) as git:
            notes.evidence("0.0.3")
            self.assertIn("v0.0.1", git.call_args.args[0])

    def test_large_context_is_losslessly_partitioned_including_oversized_files(self):
        context = "a" * 30 + r"\ndiff --git file" + "b" * 200
        with patch.object(notes, "CONTEXT_LIMIT", 50):
            parts = notes.context_parts(context)
            self.assertEqual("".join(parts), context)
            self.assertTrue(all(0 < len(part) <= 50 for part in parts))

    def test_large_diffs_are_summarized_before_generating_notes(self):
        with patch.object(notes, "CONTEXT_LIMIT", 100), patch.object(notes, "evidence", return_value=json.dumps({"previous_tag": None, "diff": "x" * 250})), patch.object(notes, "request_notes", side_effect=lambda context, summarize=False: {"summary": "facts"} if summarize else {"en": "- Change", "ru": "- Изменение"}) as request:
            self.assertEqual(notes.generate_notes("0.0.1"), ("- Change", "- Изменение"))
            self.assertGreaterEqual(request.call_count, 4)
            self.assertNotIn("summarize", request.call_args.kwargs)

    def test_nonshrinking_summaries_fail_before_a_context_overflow(self):
        with patch.object(notes, "CONTEXT_LIMIT", 50), patch.object(notes, "evidence", return_value=json.dumps({"previous_tag": None, "diff": "x" * 100})), patch.object(notes, "request_notes", return_value={"summary": "y" * 100}):
            with self.assertRaisesRegex(RuntimeError, "reduce"):
                notes.generate_notes("0.0.1")

    @patch.dict("os.environ", {"OPENAI_API_KEY": "synthetic-key"})
    def test_structured_response_is_validated_and_credentials_are_not_redirected(self):
        result = {"status": "completed", "output": [{"type": "message", "content": [{"type": "output_text", "text": json.dumps({"en": "- Change", "ru": "- Изменение"})}]}]}
        opener = Mock()
        opener.open.return_value = io.BytesIO(json.dumps(result).encode())
        with patch.object(notes.urllib.request, "build_opener", return_value=opener):
            self.assertEqual(notes.request_notes("facts")["en"], "- Change")
        payload = json.loads(opener.open.call_args.args[0].data)
        self.assertFalse(payload["store"])
        self.assertTrue(payload["text"]["format"]["strict"])
        with self.assertRaisesRegex(RuntimeError, "redirected"):
            notes.NoRedirect().redirect_request(None, None, 302, "", {}, "https://evil.test")

    @patch.dict("os.environ", {"OPENAI_API_KEY": "synthetic-key"})
    def test_api_errors_are_redacted_and_incomplete_generations_are_rejected(self):
        opener = Mock()
        with patch.object(notes.urllib.request, "build_opener", return_value=opener):
            opener.open.side_effect = urllib.error.HTTPError("private-url", 429, "private-body", {}, None)
            with self.assertRaisesRegex(RuntimeError, r"^Release note generation failed \(HTTP 429\).$"):
                notes.request_notes("facts")
            opener.open.side_effect = None
            opener.open.return_value = io.BytesIO(b'{"status":"incomplete"}')
            with self.assertRaisesRegex(RuntimeError, "did not complete"):
                notes.request_notes("facts")


if __name__ == "__main__":
    unittest.main()
