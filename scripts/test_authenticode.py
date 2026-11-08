import base64
import os
import subprocess as sp
import sys
import tempfile
import unittest
from datetime import timedelta
from pathlib import Path
from unittest.mock import Mock, patch

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.serialization import pkcs12

import authenticode as auth


class SigningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.key, cls.cert = auth.preview_certificate()
        cls.password = "test-only-password"
        cls.pfx = pkcs12.serialize_key_and_certificates(b"fixture", cls.key, cls.cert, None,
                                                       serialization.BestAvailableEncryption(cls.password.encode()))

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.pin = self.root / "public.pem"
        self.pin.write_bytes(self.cert.public_bytes(serialization.Encoding.PEM))
        for context in (patch.object(auth, "PUBLIC_CERTIFICATE", self.pin),
                        patch.object(auth, "SECRET_DIRECTORY", self.root / "missing"),
                        patch.dict(os.environ, {"WINDOWS_SIGNING_PFX": "", "WINDOWS_SIGNING_PASSWORD": ""})):
            context.start()
            self.addCleanup(context.stop)

    def credentials(self, **changes):
        values = {"WINDOWS_SIGNING_PFX": base64.b64encode(self.pfx).decode(),
                  "WINDOWS_SIGNING_PASSWORD": self.password}
        return patch.dict(os.environ, values | changes)

    def test_release_requires_permanent_credentials_but_preview_can_use_disposable_identity(self):
        with self.assertRaisesRegex(RuntimeError, "required for releases"):
            auth.material(False)
        key, certificate = auth.material(True)
        self.assertEqual(key.public_key(), certificate.public_key())
        self.assertNotEqual(certificate.fingerprint(hashes.SHA256()), self.cert.fingerprint(hashes.SHA256()))

    def test_credentials_are_validated_and_removed_from_child_environment(self):
        with self.credentials():
            key, certificate = auth.material(False)
            self.assertEqual(key.public_key(), self.key.public_key())
            self.assertEqual(certificate, self.cert)
            self.assertNotIn("WINDOWS_SIGNING_PASSWORD", os.environ)
            self.assertNotIn("WINDOWS_SIGNING_PFX", os.environ)

    def test_wrong_password_is_not_disclosed(self):
        with self.credentials(WINDOWS_SIGNING_PASSWORD="private-invalid-password"):
            with self.assertRaisesRegex(RuntimeError, "Could not unlock") as error:
                auth.material(False)
        self.assertNotIn("private-invalid-password", str(error.exception))

    def test_partial_credentials_do_not_silently_fall_back_to_preview(self):
        with patch.dict(os.environ, {"WINDOWS_SIGNING_PASSWORD": "only-one-secret"}):
            with self.assertRaisesRegex(RuntimeError, "Both Windows signing secrets"):
                auth.material(True)

    def test_certificate_must_match_public_pin(self):
        _, other = auth.preview_certificate()
        self.pin.write_bytes(other.public_bytes(serialization.Encoding.PEM))
        with self.credentials(), self.assertRaisesRegex(RuntimeError, "does not match"):
            auth.material(False)

    def test_expired_certificate_is_rejected(self):
        with self.credentials(), patch.object(auth, "datetime") as clock:
            clock.now.return_value = self.cert.not_valid_after_utc + timedelta(seconds=1)
            with self.assertRaisesRegex(RuntimeError, "not currently valid"):
                auth.material(False)

    def test_temporary_private_key_is_removed_after_failure_and_password_is_not_exported(self):
        (self.root / ".tools").mkdir()
        identity = '"user","S-1-5-21-100"\n'
        with patch.object(auth, "ROOT", self.root), patch.object(auth, "material", return_value=(self.key, self.cert)), \
                patch.object(auth, "signing_tool", return_value=self.root / "signtool.exe"), \
                patch.object(auth.sp, "check_output", return_value=identity), patch.object(auth.sp, "run"):
            with self.assertRaisesRegex(RuntimeError, "fixture failure"):
                with auth.Signer(preview=True) as signer:
                    path = Path(signer.environment["MEGAPROXY_SIGNING_PFX"])
                    self.assertTrue(path.is_file())
                    self.assertFalse("WINDOWS_SIGNING_PASSWORD" in signer.environment)
                    raise RuntimeError("fixture failure")
            self.assertFalse(path.exists())

    def test_signing_only_expected_untrusted_root_is_accepted(self):
        path = self.root / "fixture.exe"
        path.write_bytes(b"fixture")
        environment = {"MEGAPROXY_SIGNTOOL": "signtool.exe", "MEGAPROXY_SIGNING_PFX": "private.pfx"}
        for message, success in [("certificate which is not trusted\nNumber of errors: 1", True),
                                  ("Hash mismatch\nNumber of errors: 1", False),
                                  ("certificate which is not trusted\nNumber of errors: 2", False)]:
            results = [Mock(returncode=0), Mock(returncode=1, stdout=message, stderr="")]
            with patch.object(auth.sp, "run", side_effect=results):
                if success:
                    auth.sign_file(path, environment)
                else:
                    with self.assertRaisesRegex(RuntimeError, "verification failed"):
                        auth.sign_file(path, environment)

    def test_upstream_dependencies_are_not_resigned(self):
        names = ["WindowsMegaProxy.exe", "WindowsMegaProxy.Runtime.dll", "Microsoft.dll", "sing-box.exe", "settings.json"]
        for name in names:
            (self.root / name).touch()
        signer = object.__new__(auth.Signer)
        signer.environment = {}
        with patch.object(auth, "sign_file") as sign:
            signer.sign_directory(self.root)
        self.assertEqual({call.args[0].name for call in sign.call_args_list}, set(names[:2]))

    def test_old_tag_setup_uses_current_downloader_and_tag_dependency_versions(self):
        scripts = self.root / "scripts"
        scripts.mkdir()
        (scripts / "common.py").write_text('raise AssertionError("Old downloader must not be imported")\n')
        (scripts / "dependencies.py").write_text('CORE_VERSION = "tag-fixture"\n')
        (scripts / "setup.py").write_text(
            'from common import ROOT, open_download\n'
            'from dependencies import CORE_VERSION\n'
            'assert CORE_VERSION == "tag-fixture"\n'
            'assert ROOT.resolve() == __import__("pathlib").Path.cwd().resolve()\n'
            '(ROOT / "bootstrap-passed.txt").write_text(CORE_VERSION)\n')
        runner = Path(auth.__file__).with_name("release_package.py")
        sp.run([sys.executable, str(runner), "--setup"], cwd=self.root, check=True, capture_output=True)
        self.assertEqual((self.root / "bootstrap-passed.txt").read_text(), "tag-fixture")


if __name__ == "__main__":
    unittest.main()
