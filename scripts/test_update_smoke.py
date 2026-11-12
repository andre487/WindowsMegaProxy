import base64
import json
import os
import subprocess as sp
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

import update_smoke


class UpdateSmokeSafetyTests(unittest.TestCase):
    def test_fixture_references_installed_release_instead_of_current_projects(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            application, data, logs = root / "old app", root / "data", root / "logs"
            with patch.object(update_smoke, "dotnet") as dotnet:
                update_smoke.seed_settings(application, data, logs)
            project = logs / "baseline-fixture/BaselineFixture.csproj"
            document = ET.parse(project)
            self.assertEqual(document.findall(".//ProjectReference"), [])
            for reference in document.findall(".//Reference"):
                self.assertEqual(Path(reference.findtext("HintPath")), application / (reference.attrib["Include"] + ".dll"))
            dotnet.assert_called_once_with("run", "--project", str(project), "-c", "Release", "--", str(data))

    def test_service_failure_prints_redirected_diagnostic_and_still_fails(self):
        result = sp.CompletedProcess(["service.exe"], 1, stdout="Unsupported TLS fingerprint.")
        with patch.object(update_smoke.sp, "run", return_value=result) as run, patch("builtins.print") as output:
            with self.assertRaises(sp.CalledProcessError):
                update_smoke.service_command(Path("installed"), "--configure-mode", "service")
            self.assertEqual(run.call_args.kwargs["stderr"], sp.STDOUT)
            output.assert_called_once_with(result.stdout, flush=True)

    def test_first_release_skips_upgrade_without_installing(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch("sys.argv", ["update_smoke.py", "0.0.1"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                    patch.object(update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(update_smoke, "known_folder", return_value=Path(temporary)), \
                    patch.object(update_smoke, "service_state", return_value=None), \
                    patch.object(update_smoke, "previous_release", return_value=None), patch.object(update_smoke.sp, "run") as run:
                update_smoke.main()
                run.assert_not_called()

    def test_candidate_requires_pinned_signature_and_expected_version(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "assets").mkdir()
            key = Ed25519PrivateKey.generate()
            (root / "assets/update-public-key.txt").write_text(base64.b64encode(key.public_key().public_bytes_raw()).decode(), encoding="ascii")
            document = {"schemaVersion": 1, "platform": "win-x64", "preview": False, "version": "0.0.2", "components": {"installer": {
                "version": "0.0.2", "file": "WindowsMegaProxy-0.0.2-win-x64-setup.exe"}}}
            data = json.dumps(document).encode()
            (root / "update-manifest.json").write_bytes(data)
            (root / "update-manifest.sig").write_bytes(key.sign(data))
            with patch.object(update_smoke, "ROOT", root):
                self.assertEqual(update_smoke.signed_installer(root, "0.0.2")["version"], "0.0.2")
                with self.assertRaisesRegex(RuntimeError, "version"):
                    update_smoke.signed_installer(root, "0.0.3")
                (root / "update-manifest.json").write_bytes(data + b" ")
                with self.assertRaises(InvalidSignature):
                    update_smoke.signed_installer(root, "0.0.2")

    def test_refuses_a_developer_machine(self):
        with patch("sys.argv", ["update_smoke.py", "0.0.8"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                update_smoke.main()

    def test_refuses_existing_user_data_before_installing(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "WindowsMegaProxy").mkdir()
            with patch("sys.argv", ["update_smoke.py", "0.0.8"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                    patch.object(update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(update_smoke, "known_folder", return_value=root), patch.object(update_smoke, "download") as download:
                with self.assertRaisesRegex(RuntimeError, "existing"):
                    update_smoke.main()
                download.assert_not_called()

    def test_only_explicit_wix_baselines_support_in_place_updates(self):
        self.assertTrue(update_smoke.supports_upgrade({"format": "wix-burn"}))
        for installer in ({}, {"format": "inno"}, {"format": "unknown"}):
            self.assertFalse(update_smoke.supports_upgrade(installer))

    def test_inno_baseline_is_not_downloaded_or_installed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch("sys.argv", ["update_smoke.py", "0.3.0"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                    patch.object(update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(update_smoke, "ROOT", root), patch.object(update_smoke, "known_folder", return_value=root), \
                    patch.object(update_smoke, "service_state", return_value=None), \
                    patch.object(update_smoke, "previous_release", return_value="v0.2.1"), \
                    patch.object(update_smoke, "gh"), patch.object(update_smoke, "signed_installer", return_value={}), \
                    patch.object(update_smoke, "download") as download, patch.object(update_smoke, "dotnet") as dotnet, \
                    patch.object(update_smoke.sp, "run") as run:
                update_smoke.main()
                download.assert_not_called()
                dotnet.assert_not_called()
                run.assert_not_called()
