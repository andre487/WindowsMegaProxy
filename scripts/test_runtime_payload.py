import base64
import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import runtime_payload


class RuntimePayloadTests(unittest.TestCase):
    def fixture(self, root):
        payload = root / "payload"
        payload.mkdir()
        (payload / "launcher").mkdir()
        (payload / "WindowsMegaProxy.dll").write_bytes(b"application")
        for index, name in enumerate(runtime_payload.PACKAGES):
            directory = root / "packages" / name / "10.0.14"
            directory.mkdir(parents=True)
            archive = directory / f"{name}.10.0.14.nupkg"
            assets = {"native/coreclr.dll": b"runtime"} if index == 0 else {"lib/net10.0/PresentationFramework.dll": b"desktop"}
            assets["lib/net10.0/Microsoft.VisualBasic.dll"] = b"core forwarding" if index == 0 else b"desktop forwarding"
            with zipfile.ZipFile(archive, "w") as output:
                for path, data in assets.items():
                    output.writestr("runtimes/win-x64/" + path, data)
                    (payload / Path(path).name).write_bytes(data)
                    if index == 0:
                        (payload / "launcher" / Path(path).name).write_bytes(data)
            archive.with_suffix(".nupkg.sha512").write_text(base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode())
        return payload

    def test_runtime_is_detached_with_exact_hashes_and_official_sources(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            payload = self.fixture(root)
            files, runtime = runtime_payload.detach(payload, root, root / "packages", "10.0.14")
            self.assertEqual(len(files), 5)
            self.assertEqual([path.name for path in payload.rglob("*") if path.is_file()], ["WindowsMegaProxy.dll"])
            self.assertTrue(all(file["downloaded"] for file in files))
            self.assertFalse(runtime["bundled"])
            self.assertTrue(all(item["url"].startswith("https://api.nuget.org/v3-flatcontainer/microsoft.") for item in runtime["packages"]))
            sources = {item["Path"]: item for item in json.loads((root / "runtime-files.json").read_text())}
            self.assertEqual(sources["launcher/Microsoft.VisualBasic.dll"]["Package"], 0)
            self.assertEqual(sources["Microsoft.VisualBasic.dll"]["Package"], 1)
            self.assertEqual(sources["coreclr.dll"]["Entry"], "runtimes/win-x64/native/coreclr.dll")
            self.assertEqual(sources["coreclr.dll"]["Sha256"], hashlib.sha256(b"runtime").hexdigest())
            self.assertFalse((root / "runtime-files.iss").exists())

    def test_mismatched_publish_or_damaged_archive_does_not_delete_files(self):
        for corrupt_archive in (False, True):
            with self.subTest(archive=corrupt_archive), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                payload = self.fixture(root)
                if corrupt_archive:
                    next((root / "packages").rglob("*.nupkg")).write_bytes(b"corrupt")
                else:
                    (payload / "coreclr.dll").write_bytes(b"unexpected")
                with self.assertRaises(ValueError):
                    runtime_payload.detach(payload, root, root / "packages", "10.0.14")
                self.assertTrue((payload / "coreclr.dll").exists())
                self.assertTrue((payload / "PresentationFramework.dll").exists())


if __name__ == "__main__":
    unittest.main()
