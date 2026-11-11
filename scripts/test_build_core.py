import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import build_core


class CorePayloadTests(unittest.TestCase):
    def test_install_removes_only_legacy_binaries_for_each_variant_and_channel(self):
        for dev in (False, True):
            for experimental in (False, True):
                with self.subTest(dev=dev, experimental=experimental), tempfile.TemporaryDirectory() as temporary:
                    root = Path(temporary)
                    output, destination = root / "output", root / "installed"
                    output.mkdir()
                    destination.mkdir()
                    names = ("WindowsMegaProxy.Dev.Core.exe" if dev else "WindowsMegaProxy.Core.exe",
                             f"MegaProxy.Core.{'Experimental' if experimental else 'Stable'}.dll", "LICENSE.txt", "Core-NOTICES.txt")
                    for name in names:
                        (output / name).write_bytes(b"new")
                    for name in ("sing-box.exe", "sing-box-dev.exe", "libcronet.dll", "wintun.dll", "settings.json"):
                        (destination / name).write_bytes(b"old")
                    with patch.object(build_core, "main", return_value=output):
                        build_core.install(destination, dev=dev, experimental=experimental)
                    self.assertEqual({path.name for path in destination.iterdir()}, {*names, "wintun.dll", "settings.json"})


if __name__ == "__main__":
    unittest.main()
