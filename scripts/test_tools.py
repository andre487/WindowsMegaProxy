import hashlib
import ctypes as ct
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from PIL import Image

from artwork import preferred_color
from common import download, extract
from dependencies import asset
from install import secure_directory


class ToolsChecks(unittest.TestCase):
    def test_installer_rejects_untrusted_existing_directory_owner(self):
        api, kernel = unittest.mock.MagicMock(), unittest.mock.MagicMock()
        api.GetNamedSecurityInfoW.return_value = 0
        untrusted_sid = ct.create_unicode_buffer("S-1-5-21-100-200-300-1001")

        def owner_text(_, output):
            ct.cast(output, ct.POINTER(ct.c_wchar_p))[0] = ct.cast(untrusted_sid, ct.c_wchar_p)
            return 1

        api.ConvertSidToStringSidW.side_effect = owner_text
        with tempfile.TemporaryDirectory() as work, patch("ctypes.WinDLL", side_effect=[api, kernel]):
            with self.assertRaisesRegex(RuntimeError, "untrusted owner"):
                secure_directory(Path(work))
        api.SetFileSecurityW.assert_not_called()

    def test_installer_sets_an_exact_protected_acl(self):
        api, kernel = unittest.mock.MagicMock(), unittest.mock.MagicMock()
        api.GetNamedSecurityInfoW.return_value = 0
        trusted_sid = ct.create_unicode_buffer("S-1-5-18")

        def owner_text(_, output):
            ct.cast(output, ct.POINTER(ct.c_wchar_p))[0] = ct.cast(trusted_sid, ct.c_wchar_p)
            return 1

        api.ConvertSidToStringSidW.side_effect = owner_text
        with tempfile.TemporaryDirectory() as work, patch("ctypes.WinDLL", side_effect=[api, kernel]):
            secure_directory(Path(work), public=False)
        sddl = api.ConvertStringSecurityDescriptorToSecurityDescriptorW.call_args.args[0]
        self.assertEqual(sddl, "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")
        self.assertEqual(api.SetFileSecurityW.call_args.args[1], 0x80000005)

    def test_archive_cannot_escape_destination(self):
        with tempfile.TemporaryDirectory() as work:
            archive = Path(work) / "bad.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("../outside.txt", "bad")
            with self.assertRaises(RuntimeError):
                extract(archive, Path(work) / "target")
            self.assertFalse((Path(work) / "outside.txt").exists())

    def test_cached_download_verified_and_bad_download_not_published(self):
        with tempfile.TemporaryDirectory() as work:
            target = Path(work) / "asset.zip"
            target.write_bytes(b"verified")
            expected = hashlib.sha256(b"verified").hexdigest()
            with patch("urllib.request.urlopen", side_effect=AssertionError("Cache must avoid network")):
                self.assertEqual(download("https://example.invalid/asset", target, expected), target)
            response = unittest.mock.MagicMock()
            response.__enter__.return_value = response
            response.geturl.return_value = "https://example.invalid/asset"
            response.read.side_effect = [b"corrupted", b""]
            with patch("urllib.request.urlopen", return_value=response), self.assertRaises(RuntimeError):
                download("https://example.invalid/asset", target, "0" * 64)
            self.assertEqual(target.read_bytes(), b"verified")
            self.assertFalse(target.with_name("asset.zip.download").exists())

    def test_runtime_selects_x64_installer(self):
        files = [{"rid": "win-arm64", "url": "https://example/arm.exe"},
                 {"rid": "win-x64", "url": "https://example/x64.zip"},
                 {"rid": "win-x64", "url": "https://example/x64.exe"}]
        self.assertEqual(asset({"files": files}, ".exe"), files[2])

    def test_flag_palette_and_neutral_pixels(self):
        palette = [(255, 0, 0), (0, 0, 255)]
        self.assertEqual(preferred_color(Image.new("RGBA", (2, 2), "white"), palette), -1)
        self.assertEqual(preferred_color(Image.new("RGBA", (2, 2), "blue"), palette), 1)


if __name__ == "__main__":
    unittest.main()
