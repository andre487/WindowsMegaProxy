"""Detach app-local Microsoft runtime files and describe their verified download sources."""

import base64
import hashlib
import re
import zipfile

from common import digest


PACKAGES = ("microsoft.netcore.app.runtime.win-x64", "microsoft.windowsdesktop.app.runtime.win-x64")


def detach(payload, staging, packages, version):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid .NET runtime version.")
    sources, archives = {}, []
    for index, name in enumerate(PACKAGES):
        directory = packages / name / version
        archive = directory / f"{name}.{version}.nupkg"
        expected = base64.b64decode(archive.with_suffix(".nupkg.sha512").read_text().strip(), validate=True).hex()
        if digest(archive, "sha512") != expected:
            raise ValueError("Microsoft runtime package checksum mismatch.")
        archives.append({"url": f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg",
                         "sha256": digest(archive, "sha256"), "sha512": expected})
        with zipfile.ZipFile(archive) as package:
            for item in package.infolist():
                for prefix in ("runtimes/win-x64/native/", "runtimes/win-x64/lib/net10.0/"):
                    if item.is_dir() or not item.filename.startswith(prefix):
                        continue
                    relative = item.filename[len(prefix):]
                    if any(not re.fullmatch(r"[A-Za-z0-9_.-]+", part) or part in (".", "..") for part in relative.split("/")):
                        raise ValueError("Unsafe Microsoft runtime asset path.")
                    value = (index, item.filename, hashlib.sha256(package.read(item)).hexdigest())
                    sources.setdefault(relative.casefold(), []).append(value)
    files, statements = [], ["procedure InitializeRuntimeFiles;", "begin"]
    for index, archive in enumerate(archives):
        statements.append(f"  AddRuntimePackage('{archive['url']}', '{archive['sha256']}');")
    for path in sorted(payload.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(payload).as_posix()
        asset = relative.removeprefix("launcher/")
        candidates = sources.get(asset.casefold())
        if candidates is None:
            continue
        checksum = digest(path, "sha256")
        source = next((value for value in candidates if value[2] == checksum), None)
        if source is None:
            raise ValueError(f"Published runtime differs from the pinned Microsoft package: {relative}")
        index, entry, checksum = source
        files.append({"path": relative, "size": path.stat().st_size, "sha256": checksum, "downloaded": True})
        statements.append(f"  AddRuntimeFile({index}, '{relative.replace('/', chr(92))}', '{entry.replace('/', chr(92))}', '{checksum}');")
    if not any(file["path"] == "coreclr.dll" for file in files) or not any(file["path"] == "PresentationFramework.dll" for file in files):
        raise ValueError("The app-local .NET runtime is incomplete.")
    statements += ["end;", ""]
    (staging / "runtime-files.iss").write_text("\n".join(statements), encoding="utf-8")
    for file in files:
        (payload / file["path"]).unlink()
    return files, {"version": version, "bundled": False, "appLocal": True, "packages": archives}
