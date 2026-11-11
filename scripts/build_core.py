"""Build isolated, version-pinned sing-box DLLs with a shared C interface."""

import argparse
import base64
import hashlib
import os
import re
import shutil
import subprocess as sp
from pathlib import Path

from build_diagnostics import ensure_go
from common import ROOT, cli, download, extract

ZIG_VERSION = "0.14.1"
ZIG_SHA256 = "554f5378228923ffd558eac35e21af020c73789d87afeabf4bfd16f2e6feed2c"


def main(dev=False, tidy=False, publisher=None):
    output = ROOT / "artifacts" / ("dev/cores" if dev else "cores")
    source = ROOT / "native/core"
    inputs = sorted(path for path in source.iterdir() if path.name != "host_certificate.h")
    publisher = Path(publisher) if publisher else ROOT / "assets/authenticode-certificate.pem"
    inputs += [Path(__file__), Path(__file__).with_name("build_diagnostics.py"), publisher, ROOT / "LICENSE"]
    fingerprint = hashlib.sha256(b"".join(path.read_bytes() for path in inputs) + str(dev).encode()).hexdigest()
    stamp = output / "build.sha256"
    expected = [f"MegaProxy.Core.{name}.dll" for name in ("Stable", "Experimental")]
    expected += ["WindowsMegaProxy.Dev.Core.exe" if dev else "WindowsMegaProxy.Core.exe", "Core-NOTICES.txt", "LICENSE.txt"]
    if not tidy and stamp.exists() and stamp.read_text() == fingerprint and all((output / name).is_file() for name in expected):
        return output
    zig = ROOT / f".tools/zig-x86_64-windows-{ZIG_VERSION}/zig.exe"
    if not zig.exists():
        archive = download(f"https://ziglang.org/download/{ZIG_VERSION}/zig-x86_64-windows-{ZIG_VERSION}.zip",
                           ROOT / f".tools/downloads/zig-{ZIG_VERSION}.zip", ZIG_SHA256)
        extract(archive, ROOT / ".tools")
    go = ensure_go()
    output.mkdir(parents=True, exist_ok=True)
    certificate = publisher.read_text(encoding="ascii")
    der = base64.b64decode("".join(line for line in certificate.splitlines() if not line.startswith("---")), validate=True)
    header = source / "host_certificate.h"
    header.write_text("static const unsigned char host_certificate[] = {" +
                      ",".join(str(value) for value in der) + "};\n", encoding="ascii")
    env = os.environ.copy()
    env.update(GOTOOLCHAIN="local", GOMODCACHE=str(ROOT / ".tools/go-mod"),
               GOCACHE=str(ROOT / ".tools/go-cache"), CGO_ENABLED="1", GOOS="windows", GOARCH="amd64",
               CC=f'"{zig}" cc -target x86_64-windows-gnu',
               CGO_CFLAGS="-DMEGAPROXY_DEV" if dev else "",
               ZIG_GLOBAL_CACHE_DIR=str(ROOT / ".tools/zig-cache"))
    notices = [(ROOT / "LICENSE").read_text(encoding="utf-8"),
               "Go runtime\n" + (ROOT / ".tools/go/LICENSE").read_text(encoding="utf-8")]
    modules = set()
    for name, modfile in (("Stable", "go.mod"), ("Experimental", "experimental.mod")):
        if tidy:
            sp.run([str(go), "mod", "tidy", f"-modfile={modfile}"], cwd=source, env=env, check=True)
        version = re.search(r"github.com/sagernet/sing-box v(\S+)", (source / modfile).read_text())[1]
        sp.run([str(go), "build", "-mod=readonly", f"-modfile={modfile}", "-trimpath",
                "-tags=with_quic,with_utls,with_clash_api", "-buildmode=c-shared",
                f"-ldflags=-s -w -X github.com/sagernet/sing-box/constant.Version={version}",
                "-o", str(output / f"MegaProxy.Core.{name}.dll"), "."], cwd=source, env=env, check=True)
        modules.update(sp.check_output([str(go), "list", "-deps", f"-modfile={modfile}",
                                      "-tags=with_quic,with_utls,with_clash_api", "-f",
                                      "{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}", "."],
                                     cwd=source, env=env, text=True).splitlines())
    for entry in sorted(modules - {""}):
        name, version, directory = entry.split("|", 2)
        if not version:
            continue
        licenses = sorted(path for path in Path(directory).iterdir()
                          if path.is_file() and path.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")))
        if not licenses:
            raise RuntimeError(f"Missing license for {name} {version}")
        notices.append(f"\n{name} {version}\n" + "\n".join(path.read_text(encoding="utf-8") for path in licenses))
    (output / "Core-NOTICES.txt").write_text("\n".join(notices), encoding="utf-8")
    host = output / "host.c"
    shutil.copy2(source / "host.c.template", host)
    shutil.copy2(source / "host_trust.c", output / "host_trust.c")
    shutil.copy2(header, output / "host_certificate.h")
    sp.run([str(zig), "cc", "-target", "x86_64-windows-gnu", "-O2", "-municode",
            *(["-DMEGAPROXY_DEV"] if dev else []), str(host), "-lwintrust", "-lcrypt32",
            "-o", str(output / ("WindowsMegaProxy.Dev.Core.exe" if dev else "WindowsMegaProxy.Core.exe"))],
           env=env, check=True)
    shutil.copy2(ROOT / "LICENSE", output / "LICENSE.txt")
    if not tidy:
        stamp.write_text(fingerprint, encoding="ascii")
    return output


def install(destination, *, experimental=False, dev=False, publisher=None):
    output = main(dev=dev, publisher=publisher)
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for name in ("WindowsMegaProxy.Dev.Core.exe" if dev else "WindowsMegaProxy.Core.exe",
                 f"MegaProxy.Core.{'Experimental' if experimental else 'Stable'}.dll", "LICENSE.txt", "Core-NOTICES.txt"):
        shutil.copy2(output / name, destination / name)
    for name in ("sing-box.exe", "sing-box-dev.exe", "libcronet.dll"):
        (destination / name).unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--dev", action="store_true")
    parser.add_argument("--tidy", action="store_true")
    args = parser.parse_args()
    cli(lambda: main(args.dev, args.tidy))
