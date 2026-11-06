import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from PIL import Image

from artwork import preferred_color
from common import download, extract
from dependencies import asset


class ToolsChecks(unittest.TestCase):
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
