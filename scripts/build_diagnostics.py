import argparse
import os
import subprocess as sp
from pathlib import Path

from common import ROOT, cli, download, extract

GO_VERSION = "1.27.3"
GO_SHA256 = "f26081f438319757f8a1b46d7d0b2c5ec7583d4038e485e0aa725cfde7885fff"


def ensure_go():
    executable = ROOT / ".tools/go/bin/go.exe"
    if not executable.exists():
        archive = download(f"https://go.dev/dl/go{GO_VERSION}.windows-amd64.zip",
                           ROOT / f".tools/downloads/go{GO_VERSION}.zip", GO_SHA256)
        extract(archive, ROOT / ".tools")
    if (ROOT / ".tools/go/VERSION").read_text(encoding="utf-8").splitlines()[0] != f"go{GO_VERSION}":
        raise RuntimeError(f"The local Go SDK must be {GO_VERSION}; replace .tools/go before building.")
    return executable


def main(test=False):
    executable = ensure_go()
    source = ROOT / "native/diagnostics"
    output = ROOT / "artifacts/diagnostics/WindowsMegaProxy.Diagnostics.exe"
    output.parent.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update(GOTOOLCHAIN="local", GOMODCACHE=str(ROOT / ".tools/go-mod"),
               GOCACHE=str(ROOT / ".tools/go-cache"), CGO_ENABLED="0", GOOS="windows", GOARCH="amd64")
    sp.run([str(executable), "build", "-trimpath", "-ldflags=-s -w", "-o", str(output), "."],
           cwd=source, env=env, check=True)
    modules = sp.check_output([str(executable), "list", "-deps", "-f", "{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}", "."],
                              cwd=source, env=env, text=True)
    notices = ["MegaProxy.Diagnostics is licensed under GPL-3.0-or-later. Source: https://github.com/andre487/WindowsMegaProxy/tree/main/native/diagnostics\n",
               (source / "LICENSE").read_text(encoding="utf-8"),
               "Go runtime\n" + (ROOT / ".tools/go/LICENSE").read_text(encoding="utf-8")]
    for module in sorted(set(modules.splitlines()) - {""}):
        name, version, directory = module.split("|", 2)
        if not version or not directory:
            continue
        licenses = sorted(path for path in Path(directory).iterdir()
                          if path.is_file() and path.name.upper().startswith(("LICENSE", "COPYING", "NOTICE")))
        if not licenses:
            raise RuntimeError(f"Missing license for {name} {version}")
        notices.append(f"\n{name} {version}\n" + "\n".join(path.read_text(encoding="utf-8") for path in licenses))
    (output.parent / "Diagnostics-NOTICES.txt").write_text("\n".join(notices), encoding="utf-8")
    if test:
        sp.run([str(executable), "test", "-count=1", "-timeout=60s", "./..."], cwd=source, env=env, check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--test", action="store_true")
    cli(lambda: main(parser.parse_args().test))
