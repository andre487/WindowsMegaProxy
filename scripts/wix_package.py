"""Build the Burn installer; .NET and official network cores remain external downloads."""

import json
import os
import subprocess as sp
import uuid
import zipfile

from common import ROOT, digest, dotnet
from dependencies import core_package

WIX_VERSION = "7.0.0"
EXTENSIONS = ("Netfx",)


def setup_manifest(payload, version, version_directory):
    cores = []
    for folder, http3 in (("sing-box", False), ("sing-box-h3", True)):
        upstream = core_package(http3=http3)
        files = {name: digest(payload / "dependencies" / folder / name, "sha256")
                 for name in ("sing-box.exe", "libcronet.dll", "LICENSE")}
        cores.append({"folder": folder, "url": upstream["url"], "sha256": upstream["sha256"],
                      "directory": upstream["name"], "files": files})
    return {"version": version, "buildId": str(uuid.uuid4()), "versionDirectory": version_directory, "cores": cores}


def build_installer(staging, payload, definitions, runtime_download):
    tool = ROOT / ".tools/wix/wix.exe"
    if not tool.exists():
        dotnet("tool", "install", "wix", "--version", WIX_VERSION, "--tool-path", str(tool.parent))
    env = os.environ.copy()
    env.update(DOTNET_ROOT=str(ROOT / ".tools/dotnet"), DOTNET_CLI_HOME=str(ROOT / ".tools/cli"),
               NUGET_PACKAGES=str(ROOT / ".tools/nuget"))
    cache = ROOT / "artifacts/wix-cache"
    cache.mkdir(parents=True, exist_ok=True)

    def wix(*args):
        sp.run([str(tool), *map(str, args), "-acceptEula", "wix7"], cwd=cache, env=env, check=True)

    for extension in EXTENSIONS:
        wix("extension", "add", f"WixToolset.{extension}.wixext/{WIX_VERSION}")
    resources = staging / "setup-resources"
    resources.mkdir()
    with zipfile.ZipFile(resources / "payload.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(payload.rglob("*")):
            if file.is_symlink():
                raise ValueError("Installer payload cannot contain symbolic links.")
            if file.is_file() and "dependencies" not in file.relative_to(payload).parts:
                archive.write(file, file.relative_to(payload).as_posix())
    manifest = setup_manifest(payload, definitions["AppVersion"], definitions["VersionDirectory"])
    (resources / "setup.json").write_text(json.dumps(manifest), encoding="utf-8")
    setup = staging / "setup-engine"
    dotnet("publish", str(ROOT / "src/WindowsMegaProxy.Setup"), "-c", "Release", "-r", "win-x64",
           "--self-contained", "false", "-o", str(setup), f"-p:InstallerResources={resources}")
    sp.run([str(setup / "WindowsMegaProxy.Setup.exe"), "--self-test"], env=env, check=True)
    ui = staging / "setup-ui"
    dotnet("build", str(ROOT / "src/WindowsMegaProxy.SetupUI"), "-c", "Release", "-o", str(ui))
    if os.environ.get("GITHUB_ACTIONS") == "true":
        sp.run([str(ui / "WindowsMegaProxy.SetupUI.exe"), "--ui-checks", str(ROOT / "artifacts/installer-checks/ui")], check=True)
    registration = staging / "MegaProxy-registration.msi"
    wix("build", ROOT / "installer/wix/Registration.wxs", "-arch", "x64", "-d", f"AppVersion={definitions['AppVersion']}", "-o", registration)
    output = staging / f"WindowsMegaProxy-{definitions['AppVersion']}-win-x64-setup.exe"
    arguments = ["build", ROOT / "installer/wix/Bundle.wxs", "-arch", "x64", "-o", output]
    values = definitions | {"SetupDirectory": str(setup), "UIDirectory": str(ui), "SetupBuildId": manifest["buildId"],
                            "InstallSize": sum(file.stat().st_size for file in payload.rglob("*") if file.is_file()),
                            "RegistrationPackage": str(registration), "RuntimeInstaller": str(runtime_download)}
    for name, value in values.items():
        arguments.extend(["-d", f"{name}={value}"])
    for extension in EXTENSIONS:
        arguments.extend(["-ext", f"WixToolset.{extension}.wixext/{WIX_VERSION}"])
    wix(*arguments)
    return output
