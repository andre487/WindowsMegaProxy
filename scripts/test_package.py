import base64
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives.asymmetric.ed25519 import (
    Ed25519PrivateKey,
    Ed25519PublicKey,
)

import package
import prepare_release
import wix_package
from dependencies import core_package
from package import archive, checksums, merge, signed_manifest, version


class PackageTests(unittest.TestCase):
    def test_wix_manifest_pins_only_official_cores_including_license_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary)
            for folder in ("sing-box", "sing-box-h3"):
                directory = payload / "dependencies" / folder
                directory.mkdir(parents=True)
                for name in ("sing-box.exe", "libcronet.dll", "LICENSE"):
                    (directory / name).write_bytes((folder + name).encode())
            manifest = wix_package.setup_manifest(payload, "0.0.2", "0.0.2-test")
            for core, http3 in zip(manifest["cores"], (False, True)):
                upstream = core_package(http3=http3)
                self.assertEqual(core["url"], upstream["url"])
                self.assertEqual(core["sha256"], upstream["sha256"])
                self.assertEqual(core["directory"], upstream["name"])
                self.assertEqual(set(core["files"]), {"sing-box.exe", "libcronet.dll", "LICENSE"})
                for name, expected in core["files"].items():
                    self.assertEqual(expected, package.digest(payload / "dependencies" / core["folder"] / name, "sha256"))
            self.assertNotEqual(manifest["buildId"], wix_package.setup_manifest(payload, "0.0.2", "0.0.2-test")["buildId"])

    def test_production_signing_key_must_match_the_key_pinned_in_the_application(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "assets").mkdir()
            wrong = Ed25519PrivateKey.generate().public_key().public_bytes_raw()
            (root / "assets/update-public-key.txt").write_text(base64.b64encode(wrong).decode())
            encoded = base64.b64encode(Ed25519PrivateKey.generate().private_bytes_raw()).decode()
            with patch.object(package, "ROOT", root), patch.object(package, "version", return_value="0.0.1"), \
                 patch.object(package.sp, "check_output", return_value="a" * 40), patch.dict("os.environ", UPDATE_SIGNING_KEY=encoded), \
                 patch("sys.argv", ["package.py", "--skip-build"]):
                with self.assertRaisesRegex(RuntimeError, "pinned"):
                    package.main()

    def test_checksums_use_portable_gnu_line_endings(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "file.zip").write_bytes(b"abc")
            checksums(root, ["file.zip"])
            self.assertEqual((root / "SHA256SUMS").read_bytes(),
                             b"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  file.zip\n")

    def test_release_preparation_changes_both_versions_and_preserves_notes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "Directory.Build.props").write_text("<Project>\n  <Version>0.0.1</Version>\n  <Version>0.0.1-dev</Version>\n</Project>\n")
            notes = "Keep `code`, $() and Markdown literally."
            with patch.object(prepare_release, "ROOT", root), patch.object(prepare_release, "version", return_value="0.0.1"), \
                 patch.object(prepare_release, "generate_notes", return_value=(notes, "Описание")) as generate, \
                 patch.object(prepare_release, "existing_release_pr", return_value=None), \
                 patch.object(prepare_release.sp, "run") as run, patch.object(prepare_release.sp, "check_output", return_value=""), \
                 patch.dict("os.environ", GH_TOKEN="test", RELEASE_VERSION="0.0.2"):
                prepare_release.main()
            generate.assert_called_once_with("0.0.2")
            text = (root / "Directory.Build.props").read_text()
            self.assertIn("<Version>0.0.2</Version>", text)
            self.assertIn("<Version>0.0.2-dev</Version>", text)
            self.assertIn(notes, (root / "release-notes/v0.0.2.md").read_text(encoding="utf-8"))
            self.assertFalse(any("--force" in call.args[0] for call in run.call_args_list))
            self.assertEqual(run.call_args_list[-1].args[0][:3], ["gh", "pr", "create"])

    def test_release_preparation_rejects_versions_before_git_mutations(self):
        for target in ("1.2", "v1.2.3", "1.02.3", "65536.0.0", "0.0.0", "1.0.0; echo unsafe"):
            with patch.dict("os.environ", GH_TOKEN="test", RELEASE_VERSION=target), \
                 patch.object(prepare_release, "version", return_value="0.0.1"), patch.object(prepare_release.sp, "run") as run:
                with self.assertRaises(ValueError):
                    prepare_release.main()
                run.assert_not_called()

    def test_merge_rejects_conflicting_shared_assemblies(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, target = root / "source", root / "target"
            source.mkdir()
            target.mkdir()
            (source / "shared.dll").write_bytes(b"one")
            merge(source, target)
            merge(source, target)
            (source / "shared.dll").write_bytes(b"two")
            with self.assertRaisesRegex(ValueError, "Conflicting"):
                merge(source, target)
            self.assertEqual((target / "shared.dll").read_bytes(), b"one")

    def test_app_and_core_archives_have_independent_root_relative_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = root / "payload"
            core = payload / "dependencies/sing-box"
            core.mkdir(parents=True)
            (core / "libcronet.dll").write_bytes(b"cronet")
            (payload / "WindowsMegaProxy.exe").write_bytes(b"app")
            files = archive(payload, root / "app.zip", exclude=("dependencies",))
            self.assertEqual([file["path"] for file in files], ["WindowsMegaProxy.exe"])
            archive(core, root / "core.zip", prefix="dependencies/sing-box/")
            with zipfile.ZipFile(root / "core.zip") as output:
                self.assertEqual(output.namelist(), ["dependencies/sing-box/libcronet.dll"])

    def test_signature_authenticates_exact_manifest_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            signed_manifest({"preview": True, "version": version()}, root, Ed25519PrivateKey.generate())
            key = Ed25519PublicKey.from_public_bytes(base64.b64decode((root / "update-public-key.txt").read_text()))
            data = (root / "update-manifest.json").read_bytes()
            signature = (root / "update-manifest.sig").read_bytes()
            key.verify(signature, data)
            self.assertTrue(json.loads(data)["preview"])
            with self.assertRaises(InvalidSignature):
                key.verify(signature, data + b" ")
            with self.assertRaises(InvalidSignature):
                Ed25519PrivateKey.generate().public_key().verify(signature, data)


if __name__ == "__main__":
    unittest.main()
