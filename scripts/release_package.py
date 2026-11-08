"""Use current packaging/signing automation with the selected release checkout."""

import importlib
import runpy
import sys
from pathlib import Path

import common

if __name__ == "__main__":
    common.ROOT = Path.cwd()
    importlib.import_module("authenticode")
    # Application build and dependency versions belong to the tag being rebuilt.
    sys.path.insert(0, str(common.ROOT / "scripts"))
    runpy.run_path(str(Path(__file__).with_name("package.py")), run_name="__main__")
