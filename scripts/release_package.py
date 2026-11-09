"""Use current packaging/signing automation with the selected release checkout."""

import importlib
import runpy
import sys
from pathlib import Path

import common

if __name__ == "__main__":
    common.ROOT = Path.cwd()
    if sys.argv[1:2] == ["--verify-upgrade"]:
        sys.argv = [sys.argv[0], *sys.argv[2:]]
        runpy.run_path(str(Path(__file__).with_name("update_smoke.py")), run_name="__main__")
        sys.exit(0)
    importlib.import_module("authenticode")
    # Application build and dependency versions belong to the tag being rebuilt.
    sys.path.insert(0, str(common.ROOT / "scripts"))
    script = Path(__file__).with_name("package.py")
    if sys.argv[1:] == ["--setup"]:
        script = common.ROOT / "scripts/setup.py"
        sys.argv = sys.argv[:1]
    runpy.run_path(str(script), run_name="__main__")
