import io
import json
import shutil
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import Mock, patch

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

import publish_release as release
import virus_total as vt
from package import checksums, signed_manifest

TAG = "v0.0.1"
COMMIT = "a" * 40
REPO = "owner/project"


def fixture(directory, extra=b""):
    directory.mkdir(parents=True, exist_ok=True)
    components = {}
    for component, name in release.package_names(TAG).items():
        (directory / name).write_bytes(component.encode() + extra)
        components[component] = {"file": name, "version": TAG[1:], "sha256": release.digest(directory / name), "size": (directory / name).stat().st_size}
    signed_manifest({"schemaVersion": 1, "version": TAG[1:], "commit": COMMIT, "preview": False, "components": components}, directory, Ed25519PrivateKey.generate())
    checksums(directory, sorted(set(release.package_names(TAG).values()) | (release.METADATA - {"SHA256SUMS"})))
    rows = [row(path) for path in vt.targets(directory)]
    vt.write_report(directory, rows, True)
    return rows


def row(path, malicious=0):
    digest = release.digest(path)
    return {"file": path.name, "sha256": digest, "url": f"https://www.virustotal.com/gui/file/{digest}",
            "analysis_id": "synthetic-id", "stats": {"malicious": malicious, "suspicious": 0, "undetected": 60}}


class VirusTotalTests(unittest.TestCase):
    def test_existing_hash_reports_are_reused_without_an_api_key(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fixture(directory)
            with patch.object(vt, "Client") as client:
                vt.scan_artifacts(directory)
            client.assert_not_called()
            self.assertEqual(len(json.loads((directory / "VIRUSTOTAL.json").read_text())), 2)

    def test_changed_artifacts_are_not_submitted_under_an_old_manifest_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fixture(directory)
            target = vt.targets(directory)[0]
            target.write_bytes(b"changed")
            with patch.object(vt, "Client") as client, self.assertRaisesRegex(RuntimeError, "manifest"):
                vt.scan_artifacts(directory)
            client.assert_not_called()

    def test_partial_scan_retains_the_completed_report_and_blocks_publication(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            rows = fixture(directory)
            (directory / "VIRUSTOTAL.json").write_text(json.dumps(rows[:1]))
            with patch.object(vt, "Client") as client:
                client.return_value.scan.side_effect = RuntimeError("VirusTotal API failed (HTTP 429)")
                with self.assertRaisesRegex(RuntimeError, "429"):
                    vt.scan_artifacts(directory)
            self.assertEqual(len(json.loads((directory / "VIRUSTOTAL.json").read_text())), 1)
            self.assertIn("Scan incomplete", (directory / "VIRUSTOTAL.md").read_text())
            with self.assertRaises(RuntimeError):
                release.verify(directory, TAG, COMMIT, reports=True)

    def test_detected_files_are_rescanned_on_retry_and_only_fresh_clean_results_unblock(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            rows = fixture(directory)
            rows[0]["stats"]["malicious"] = 1
            (directory / "VIRUSTOTAL.json").write_text(json.dumps(rows))
            with patch.object(vt, "Client") as client:
                client.return_value.scan.return_value = rows[0]
                with self.assertRaisesRegex(RuntimeError, "detections"):
                    vt.scan_artifacts(directory)
                client.return_value.scan.assert_called_once()
                client.return_value.scan.return_value = row(vt.targets(directory)[0])
                vt.scan_artifacts(directory)
            self.assertEqual(json.loads((directory / "VIRUSTOTAL.json").read_text())[0]["stats"]["malicious"], 0)

    def test_upload_urls_and_redirects_cannot_expose_credentials(self):
        client = vt.Client("a" * 64)
        client.opener = Mock()
        for url in ("http://www.virustotal.com/upload", "https://evil.test/upload", "https://www.virustotal.com.evil.test/upload", "https://user@www.virustotal.com/upload", "https://www.virustotal.com:444/upload"):
            with self.assertRaises(RuntimeError):
                client.request(url)
        client.opener.open.assert_not_called()
        with self.assertRaisesRegex(RuntimeError, "redirected"):
            vt.NoRedirect().redirect_request(None, None, 302, "", {}, "https://evil.test")

    def test_large_upload_uses_upload_url_and_waits_for_completed_analysis(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "setup.exe"
            path.write_bytes(b"x" * (32 * 1024 * 1024))
            client = vt.Client("a" * 64)
            client.opener = Mock()
            responses = [{"data": "https://bigfiles.virustotal.com/upload"}, {"data": {"id": "analysis/id"}},
                         {"data": {"attributes": {"status": "queued"}}},
                         {"data": {"attributes": {"status": "completed", "stats": {"malicious": 0, "suspicious": 0, "undetected": 60}}}, "meta": {"file_info": {"sha256": release.digest(path)}}}]
            client.opener.open.side_effect = [io.BytesIO(json.dumps(value).encode()) for value in responses]
            with patch.object(vt.time, "sleep"):
                result = client.scan(path)
            urls = [call.args[0].full_url for call in client.opener.open.call_args_list]
            self.assertEqual(urls[:2], [vt.API + "files/upload_url", "https://bigfiles.virustotal.com/upload"])
            self.assertIn("analysis%2Fid", urls[-1])
            self.assertTrue(vt.valid_row(result, path))

    def test_deadline_and_quota_errors_are_bounded_and_redacted(self):
        client = vt.Client("a" * 64)
        client.deadline = 0
        with self.assertRaisesRegex(RuntimeError, "deadline"):
            client.request(vt.API + "files/upload_url")
        client = vt.Client("a" * 64)
        client.opener = Mock()
        client.opener.open.side_effect = urllib.error.HTTPError("secret-upload-url", 429, "secret-response", {}, None)
        with self.assertRaisesRegex(RuntimeError, r"^VirusTotal API failed \(HTTP 429\)$"):
            client.request(vt.API + "files/upload_url")

    def test_unusable_engine_results_or_another_file_hash_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "setup.exe"
            path.write_bytes(b"synthetic application")
            cases = [({}, None), ({"malicious": 0, "suspicious": 0, "undetected": 0, "failure": 60}, None),
                     ({"malicious": 0, "suspicious": 0, "undetected": -1}, None),
                     ({"malicious": 0, "suspicious": 0, "undetected": True}, None),
                     ({"malicious": 0, "suspicious": 0, "undetected": 60}, "f" * 64)]
            for stats, reported in cases:
                client = vt.Client("a" * 64)
                response = {"data": {"attributes": {"status": "completed", "stats": stats}}}
                if reported:
                    response["meta"] = {"file_info": {"sha256": reported}}
                with patch.object(client, "request", side_effect=[{"data": {"id": "analysis"}}, response]), self.assertRaises(RuntimeError):
                    client.scan(path)


class ReleaseRetryTests(unittest.TestCase):
    def test_github_failures_do_not_log_response_bodies(self):
        with patch.object(release.sp, "run", return_value=Mock(returncode=1, stderr="private-response", stdout="private-response")):
            with self.assertRaisesRegex(RuntimeError, "^GitHub command failed: release upload$"):
                release.gh("release", "upload", TAG)

    def test_manifest_signature_hashes_and_reports_are_required(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fixture(directory)
            release.verify(directory, TAG, COMMIT, reports=True)
            with self.assertRaises(RuntimeError):
                release.verify(directory, TAG, "b" * 40)
            path = directory / release.package_names(TAG)["app"]
            path.write_bytes(b"changed")
            with self.assertRaises(RuntimeError):
                release.verify(directory, TAG, COMMIT)

    def test_only_a_real_404_is_treated_as_a_missing_release(self):
        for error, missing in (("gh: Not Found (HTTP 404)", True), ("gh: Forbidden (HTTP 403)", False), ("network unavailable", False)):
            with patch.object(release.sp, "run", return_value=Mock(returncode=1, stderr=error)):
                if missing:
                    self.assertIsNone(release.release_state(REPO, TAG))
                else:
                    with self.assertRaises(RuntimeError):
                        release.release_state(REPO, TAG)

    def test_published_retry_only_updates_notes_without_upload_or_delete(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "release"
            fixture(directory)
            state = {"draft": False, "body": "User notes\n\n" + release.START + "\nold report\n" + release.END}
            def download(_, __, ___, destination):
                shutil.copytree(directory, destination, dirs_exist_ok=True)
                return True
            notes = []
            def gh(*args):
                self.assertEqual(args[:2], ("release", "edit"))
                notes.append(Path(args[args.index("--notes-file") + 1]).read_text())
            with patch.object(release, "release_state", return_value=state), patch.object(release, "download_release", side_effect=download), patch.object(release, "gh", side_effect=gh):
                release.publish(directory, REPO, TAG, COMMIT, Path("unused"))
            self.assertEqual(notes[0].count(release.START), 1)
            self.assertIn("User notes", notes[0])

    def test_published_files_cannot_be_replaced_by_a_rebuild(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory, original = Path(temporary) / "rebuilt", Path(temporary) / "original"
            fixture(directory, b"rebuilt")
            fixture(original)
            def download(_, __, ___, destination):
                shutil.copytree(original, destination, dirs_exist_ok=True)
                return True
            with patch.object(release, "release_state", return_value={"draft": False}), patch.object(release, "download_release", side_effect=download), patch.object(release, "gh") as gh:
                with self.assertRaisesRegex(RuntimeError, "refusing to replace"):
                    release.publish(directory, REPO, TAG, COMMIT, Path("unused"))
            gh.assert_not_called()

    def test_missing_or_interrupted_draft_can_be_resumed_without_deleting_it(self):
        for state in (None, {"draft": True}):
            with tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary) / "release"
                fixture(directory)
                notes = Path(temporary) / "notes.md"
                notes.write_text("Committed notes")
                with patch.object(release, "release_state", side_effect=[state, {"draft": True}]), patch.object(release, "gh") as gh:
                    release.publish(directory, REPO, TAG, COMMIT, notes)
                commands = [call.args for call in gh.call_args_list]
                self.assertTrue(any(command[:2] == ("release", "upload") and "--clobber" in command for command in commands))
                self.assertFalse(any(command[:2] == ("release", "delete") for command in commands))

    def test_incomplete_draft_restores_the_original_actions_checkpoint(self):
        with tempfile.TemporaryDirectory() as temporary:
            source, destination = Path(temporary) / "source", Path(temporary) / "restored"
            fixture(source)
            def gh(*args):
                if args[0] == "api":
                    return json.dumps({"artifacts": [{"name": "release-packages", "expired": False}]})
                shutil.copytree(source, Path(args[args.index("--dir") + 1]), dirs_exist_ok=True)
                return ""
            with patch.object(release, "release_state", return_value={"draft": True, "assets": []}), patch.object(release, "gh", side_effect=gh):
                self.assertTrue(release.restore(destination, REPO, TAG, COMMIT, "123"))
            self.assertEqual((destination / "update-manifest.json").read_bytes(), (source / "update-manifest.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
