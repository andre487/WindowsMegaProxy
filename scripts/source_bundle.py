"""Include the source of linked Go modules and the distributed WiX components."""

import os
import subprocess as sp
import zipfile
from pathlib import Path

from build_diagnostics import ensure_go
from common import ROOT, download

WIX_SOURCE_URL = "https://codeload.github.com/wixtoolset/wix/zip/refs/tags/v7.0.0"
WIX_SOURCE_SHA256 = "1239d8baf2d70fcd6a43516a6649055f9fc59b816d0d2cdafe2b078e540e696f"


def append_dependencies(archive, *, root=ROOT):
    root = Path(root)
    go = ensure_go()
    env = os.environ.copy()
    env.update(GOTOOLCHAIN="local", GOMODCACHE=str(ROOT / ".tools/go-mod"),
               GOCACHE=str(ROOT / ".tools/go-cache"), GOOS="windows", GOARCH="amd64")
    modules = {}
    for directory, modfile, cgo in (("core", "go.mod", "1"), ("core", "experimental.mod", "1"),
                                    ("diagnostics", "go.mod", "0")):
        env["CGO_ENABLED"] = cgo
        command = [str(go), "list", "-mod=readonly", "-deps", f"-modfile={modfile}",
                   "-f", "{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}"]
        if directory == "core":
            command.append("-tags=with_quic,with_utls,with_clash_api")
        result = sp.check_output(command + ["."], cwd=root / "native" / directory, env=env, text=True)
        for entry in result.splitlines():
            if not entry:
                continue
            name, version, location = entry.split("|", 2)
            if version:
                if not location or not Path(location).is_dir():
                    raise RuntimeError(f"Missing dependency source: {name} {version}")
                modules[f"{name}@{version}"] = Path(location)
    if not modules:
        raise RuntimeError("No linked Go dependency sources found")
    wix = download(WIX_SOURCE_URL, ROOT / ".tools/downloads/wix-v7.0.0-source.zip", WIX_SOURCE_SHA256)
    with zipfile.ZipFile(archive, "a", compression=zipfile.ZIP_DEFLATED) as output:
        for name, directory in sorted(modules.items()):
            for path in sorted(directory.rglob("*")):
                if path.is_file():
                    output.write(path, "third-party/go/" + name + "/" + path.relative_to(directory).as_posix())
        go_root = go.parent.parent
        for path in sorted((go_root / "src").rglob("*")):
            if path.is_file():
                output.write(path, "third-party/go-runtime/" + path.relative_to(go_root).as_posix())
        for name in ("LICENSE", "VERSION"):
            output.write(go_root / name, "third-party/go-runtime/" + name)
        output.write(wix, "third-party/wix-v7.0.0-source.zip", compress_type=zipfile.ZIP_STORED)
        output.writestr("third-party/README.txt", """These sources accompany this release's compiled components and retain their own licenses.
go/ contains complete module directories for the linked stable, experimental and diagnostic dependencies.
go-runtime/ contains the pinned Go runtime/library sources and license.
wix-v7.0.0-source.zip is the hash-verified upstream WiX source, including Burn and its API.
Build commands and pinned tool versions are in docs/development.md, native/core/README.md and scripts/.
Standard builds retrieve modules using go.mod/go.sum. To develop an included module locally,
use go mod edit -replace=module/path=absolute/source/path with the appropriate -modfile
(native/core/go.mod, native/core/experimental.mod, or native/diagnostics/go.mod).
Do not discard dependency copyright notices when modifying or redistributing these sources.
""")
