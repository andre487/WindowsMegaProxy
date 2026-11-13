"""Build the unprivileged Task Scheduler launcher without a bundled runtime."""

import os
import struct
import subprocess as sp
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

from build_core import ensure_zig
from common import ROOT, app_name


def build_launcher(output, dev=False):
    zig = ensure_zig()
    output = Path(output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = ROOT / "native/launcher"
    version = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/Version")
    commit = sp.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    title = "MegaProxy DEV" if dev else "MegaProxy"
    icon = ROOT / "src/WindowsMegaProxy/Assets" / ("dev-app.ico" if dev else "app.ico")
    env = os.environ.copy()
    env["ZIG_GLOBAL_CACHE_DIR"] = str(ROOT / ".tools/zig-cache")
    with tempfile.TemporaryDirectory(dir=output) as temporary:
        resource = Path(temporary) / "launcher.rc"
        resource.write_text(
            f'#define APP_ICON "{icon.as_posix()}"\n'
            f'#define APP_TITLE "{title}"\n'
            f'#define VERSION_NUMBER {version.replace(".", ",")},0\n'
            f'#define PRODUCT_VERSION "{version}{"-dev" if dev else ""}+{commit}"\n'
            + (source / "launcher.rc").read_text(encoding="utf-8"), encoding="utf-8")
        compiled = resource.with_suffix(".res")
        sp.run([str(zig), "rc", "/I", str(source), "/fo", str(compiled), str(resource)],
               cwd=source, env=env, check=True)
        sp.run([str(zig), "cc", "-target", "x86_64-windows-gnu", "-Os", "-s", "-municode", "-Wl,--subsystem,windows",
                *(["-DMEGAPROXY_DEV"] if dev else []), str(source / "main.c"), str(compiled),
                "-lole32", "-loleaut32", "-ladvapi32", "-luser32", "-luuid",
                "-o", str(output / (app_name(dev) + ".Launcher.exe"))], env=env, check=True)
    executable = (output / (app_name(dev) + ".Launcher.exe")).read_bytes()
    pe = struct.unpack_from("<I", executable, 0x3c)[0]
    optional = pe + 24
    assert executable[pe:pe + 4] == b"PE\0\0"
    assert struct.unpack_from("<H", executable, optional)[0] == 0x20b, "Expected a 64-bit executable."
    assert struct.unpack_from("<H", executable, optional + 68)[0] == 2, "Launcher must not open a console."
    assert struct.unpack_from("<II", executable, optional + 112 + 14 * 8) == (0, 0), "Launcher must not require .NET."
