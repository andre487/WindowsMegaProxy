import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

import source_bundle


class SourceBundleTests(unittest.TestCase):
    def test_sources_preserve_versions_licenses_and_existing_project_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sdk = root / "go"
            (sdk / "src/runtime").mkdir(parents=True)
            (sdk / "src/runtime/example.go").write_text("runtime source")
            for name in ("LICENSE", "VERSION"):
                (sdk / name).write_text(name)
            modules = []
            for version in ("v1", "v2"):
                module = root / version
                module.mkdir()
                (module / "LICENSE").write_text("upstream license " + version)
                (module / "source.go").write_text("module source " + version)
                modules.append(f"example.org/module|{version}|{module}\n")
            wix = root / "wix.zip"
            wix.write_bytes(b"verified upstream archive")
            archive = root / "source.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("LICENSE", "project license")
            with patch.object(source_bundle, "ensure_go", return_value=sdk / "bin/go.exe"), \
                 patch.object(source_bundle.sp, "check_output", side_effect=[modules[0], modules[1], modules[0]]), \
                 patch.object(source_bundle, "download", return_value=wix) as download:
                source_bundle.append_dependencies(archive, root=root)
            self.assertEqual(download.call_args.args[2], source_bundle.WIX_SOURCE_SHA256)
            with zipfile.ZipFile(archive) as output:
                self.assertEqual(len(output.namelist()), len(set(output.namelist())))
                self.assertEqual(output.read("LICENSE"), b"project license")
                for version in ("v1", "v2"):
                    self.assertEqual(output.read(f"third-party/go/example.org/module@{version}/LICENSE"),
                                     ("upstream license " + version).encode())
                self.assertEqual(output.read("third-party/go-runtime/src/runtime/example.go"), b"runtime source")
                self.assertEqual(output.read("third-party/wix-v7.0.0-source.zip"), wix.read_bytes())

    def test_missing_sources_fail_before_publishing(self):
        with patch.object(source_bundle, "ensure_go", return_value=Path("go.exe")), \
             patch.object(source_bundle.sp, "check_output", return_value="example.org/missing|v1|\n"):
            with self.assertRaisesRegex(RuntimeError, "Missing dependency source"):
                source_bundle.append_dependencies("unused.zip")


if __name__ == "__main__":
    unittest.main()
