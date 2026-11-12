import unittest
import xml.etree.ElementTree as ET

from common import ROOT


class LicenseScopesTests(unittest.TestCase):
    def test_license_boundaries_and_setup_dependencies(self):
        mit = (ROOT / "LICENSE").read_text(encoding="utf-8")
        self.assertTrue(mit.startswith("MIT License"))
        gpl = (ROOT / "installer/licenses/GPL-3.0.txt").read_text(encoding="utf-8")
        for directory in ("src", "tests", "native/core"):
            self.assertEqual((ROOT / directory / "LICENSE").read_text(encoding="utf-8"), gpl)
        for name in ("WindowsMegaProxy.Setup", "WindowsMegaProxy.SetupUI"):
            directory = ROOT / "src" / name
            self.assertEqual((directory / "LICENSE").read_text(encoding="utf-8"), mit)
            project = ET.parse(directory / (name + ".csproj"))
            self.assertEqual(project.findtext("PropertyGroup/PackageLicenseExpression"), "MIT")
            self.assertEqual(project.findall(".//ProjectReference"), [])
            for item in project.findall(".//Compile"):
                included = (directory / item.attrib["Include"]).resolve()
                self.assertTrue(included.is_relative_to(ROOT / "src/WindowsMegaProxy.Setup"))


if __name__ == "__main__":
    unittest.main()
