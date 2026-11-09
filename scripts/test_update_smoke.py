import base64
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

import update_smoke


class UpdateSmokeSafetyTests(unittest.TestCase):
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
