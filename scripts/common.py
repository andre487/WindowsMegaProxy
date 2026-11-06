"""Shared paths and standard-library helpers for the Windows development scripts."""

import hashlib
import os
from pathlib import Path
import shutil
import subprocess as sp
import sys
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
SDK_VERSION = "10.0.403"


def dotnet_path():
    local = ROOT / ".tools/dotnet/dotnet.exe"
    return str(local) if local.exists() else shutil.which("dotnet")


def dotnet(*args):
    executable = dotnet_path()
    if not executable:
        raise RuntimeError(".NET SDK is missing. Run setup.cmd first.")
    env = os.environ.copy()
    env.update(
        DOTNET_CLI_HOME=str(ROOT / ".tools/cli"),
        DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_NOLOGO="1",
        DOTNET_CLI_UI_LANGUAGE="en-US",
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="0",
        NUGET_PACKAGES=str(ROOT / ".tools/nuget"),
    )
    if Path(executable).parent == ROOT / ".tools/dotnet":
        env["DOTNET_ROOT"] = str(Path(executable).parent)
    return sp.run([executable, *args], cwd=ROOT, env=env, stdout=sys.stdout, stderr=sys.stderr, check=True)


def download(url, path, expected, algorithm="sha256"):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and digest(path, algorithm) == expected.lower():
        return path
    temporary = path.with_name(path.name + ".download")
    try:
        with urllib.request.urlopen(url, timeout=60) as response, temporary.open("wb") as output:
            if not response.geturl().startswith("https://"):
                raise RuntimeError("Refusing an insecure download redirect.")
            shutil.copyfileobj(response, output)
        if digest(temporary, algorithm) != expected.lower():
            raise RuntimeError(f"Checksum mismatch: {path.name}")
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
    return path


def digest(path, algorithm):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def extract(archive, destination):
    destination = Path(destination).resolve()
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            if not (destination / item.filename).resolve().is_relative_to(destination):
                raise RuntimeError("Archive entry escapes its destination.")
        source.extractall(destination)


def cli(main):
    try:
        main()
    except (OSError, RuntimeError, sp.SubprocessError, ValueError) as error:
        print(f"Error: {error}", file=sys.stderr)
        raise SystemExit(1)
