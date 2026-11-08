"""Official checksum-verified downloads shared by development and installation."""
import json
import shutil
from pathlib import Path

from common import SDK_VERSION, download, extract, open_download

CORE_VERSION = "1.14.4"
CORE_HASH = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df"
H3_CORE_VERSION = "1.15.0-alpha.13"
H3_CORE_HASH = "7cff6a39687a12d51b289bd57a203723629eb8fd42f6668601b4484456436bc9"


def release():
    with open_download("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json", timeout=30) as response:
        metadata = json.load(response)
    return next(item for item in metadata["releases"] if any(sdk["version"] == SDK_VERSION for sdk in item.get("sdks", [])))


def asset(component, extension):
    return next(file for file in component["files"] if file["rid"] == "win-x64" and file["url"].endswith(extension))


def core_package(*, http3=False):
    version, checksum = (H3_CORE_VERSION, H3_CORE_HASH) if http3 else (CORE_VERSION, CORE_HASH)
    name = f"sing-box-{version}-windows-amd64"
    return {"version": version, "name": name, "sha256": checksum,
            "url": f"https://github.com/SagerNet/sing-box/releases/download/v{version}/{name}.zip"}


def install_core(destination, cache, *, http3=False):
    package = core_package(http3=http3)
    name = package["name"]
    archive = download(package["url"], Path(cache) / f"{name}.zip", package["sha256"])
    expanded = Path(cache) / name
    extract(archive, expanded)
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for file in (expanded / name).iterdir():
        if file.is_file():
            shutil.copy2(file, destination / file.name)


def runtime_installer(cache):
    desktop = release()["windowsdesktop"]
    file = asset(desktop, ".exe")
    return download(file["url"], Path(cache) / f"windowsdesktop-runtime-{desktop['version']}-win-x64.exe", file["hash"], "sha512")
