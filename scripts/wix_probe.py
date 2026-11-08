"""Build a text-only MSI and Burn bundle without installing either package."""

import os
import subprocess as sp

from common import ROOT, cli, digest, dotnet

VERSION = "5.0.2"


def main():
    output = ROOT / "artifacts/wix-probe"
    output.mkdir(parents=True, exist_ok=True)
    tool = ROOT / ".tools/wix-probe/wix.exe"
    if not tool.exists():
        dotnet("tool", "install", "wix", "--version", VERSION,
               "--tool-path", str(tool.parent))
    env = os.environ.copy()
    env["DOTNET_ROOT"] = str(ROOT / ".tools/dotnet")
    env["DOTNET_CLI_HOME"] = str(ROOT / ".tools/cli")
    env["NUGET_PACKAGES"] = str(ROOT / ".tools/nuget")

    def wix(*args):
        sp.run([str(tool), *map(str, args)], cwd=output, env=env, check=True)

    wix("--version")
    wix("extension", "add", f"WixToolset.BootstrapperApplications.wixext/{VERSION}")
    source = ROOT / "installer/wix-probe"
    package = output / "control.msi"
    bundle = output / "control-burn.exe"
    wix("build", source / "Package.wxs", "-arch", "x64",
        "-d", f"SourceDirectory={source}", "-o", package)
    wix("build", source / "Bundle.wxs", "-arch", "x64",
        "-ext", f"WixToolset.BootstrapperApplications.wixext/{VERSION}",
        "-d", f"PackagePath={package}", "-o", bundle)
    for path in (package, bundle):
        if not path.is_file() or not path.stat().st_size:
            raise RuntimeError(f"Missing installer output: {path.name}")
        print(f"{path.name}: {path.stat().st_size} bytes; SHA256 {digest(path, 'sha256')}")
    if package.read_bytes()[:8] != bytes.fromhex("D0CF11E0A1B11AE1"):
        raise RuntimeError("Invalid MSI header.")
    if bundle.read_bytes()[:2] != b"MZ":
        raise RuntimeError("Invalid Burn executable header.")


if __name__ == "__main__":
    cli(main)
