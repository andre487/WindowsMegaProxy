"""Build and sign WiX/Burn with a managed UI and an app-local runtime installer."""

import json
import os
import subprocess as sp
import uuid
import zipfile

from authenticode import sign_file
from common import ROOT, digest, dotnet

WIX_VERSION = "7.0.0"


def build_installer(staging, payload, definitions, runtime, signer):
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

    dotnet("build", str(ROOT / "tests/WindowsMegaProxy.SetupChecks"), "-c", "Release")
    checks = ROOT / "tests/WindowsMegaProxy.SetupChecks/bin/Release/net462/WindowsMegaProxy.SetupChecks.exe"
    sp.run([str(checks), str(ROOT / "artifacts/installer-checks/wix-ui")], check=True, timeout=60)
    resources = staging / "setup-resources"
    resources.mkdir()
    with zipfile.ZipFile(resources / "payload.zip", "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(payload.rglob("*")):
            if file.is_symlink():
                raise ValueError("Installer payload cannot contain symbolic links.")
            if file.is_file():
                archive.write(file, file.relative_to(payload).as_posix())
    manifest = {"Version": definitions["AppVersion"], "BuildId": str(uuid.uuid4()),
                "PayloadSha256": digest(resources / "payload.zip", "sha256"),
                "Packages": [{"Url": item["url"], "Sha256": item["sha256"]} for item in runtime["packages"]],
                "RuntimeFiles": json.loads((staging / "runtime-files.json").read_text(encoding="utf-8"))}
    manifest["Files"] = [{"Path": file.relative_to(payload).as_posix(), "Sha256": digest(file, "sha256")}
                         for file in sorted(payload.rglob("*")) if file.is_file()] + manifest["RuntimeFiles"]
    (resources / "setup.json").write_text(json.dumps(manifest), encoding="utf-8")
    setup, ui = staging / "setup-engine", staging / "setup-ui"
    dotnet("build", str(ROOT / "src/WindowsMegaProxy.Setup"), "-c", "Release", "-o", str(setup), f"-p:InstallerResources={resources}")
    dotnet("build", str(ROOT / "src/WindowsMegaProxy.SetupUI"), "-c", "Release", "-o", str(ui))
    signer.sign_directory(setup)
    signer.sign_directory(ui)
    registration = staging / "MegaProxy-registration.msi"
    wix("build", ROOT / "installer/wix/Registration.wxs", "-arch", "x64", "-d", f"AppVersion={definitions['AppVersion']}", "-o", registration)
    sign_file(registration, signer.environment)
    output = staging / f"WindowsMegaProxy-{definitions['AppVersion']}-win-x64-setup.exe"
    arguments = ["build", ROOT / "installer/wix/Bundle.wxs", "-arch", "x64", "-o", output]
    values = definitions | {"SetupDirectory": str(setup), "ResourceDirectory": str(resources), "UIDirectory": str(ui), "SetupBuildId": manifest["BuildId"],
                            "InstallSize": sum(file.stat().st_size for file in payload.rglob("*") if file.is_file()),
                            "RegistrationPackage": str(registration),
                            "SetupEngineSha256": digest(setup / "WindowsMegaProxy.Setup.exe", "sha256")}
    for name, value in values.items():
        arguments.extend(["-d", f"{name}={value}"])
    wix(*arguments)
    # Burn elevates the detached engine; both the engine and the final bundle need signatures.
    engine = staging / "burn-engine.exe"
    wix("burn", "detach", output, "-engine", engine)
    sign_file(engine, signer.environment)
    signed = staging / "signed-bundle.exe"
    wix("burn", "reattach", output, "-engine", engine, "-o", signed)
    signed.replace(output)
    sign_file(output, signer.environment)
    return output
