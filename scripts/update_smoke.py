"""Exercise a published automatic update only on an empty GitHub-hosted Windows runner."""
import argparse
import base64
import ctypes as ct
import json
import os
import re
import shutil
import subprocess as sp
import time
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

from build import product_version
from common import ROOT, cli, digest, dotnet, download
from deferred_update_smoke import wait_until
from install import known_folder
from publish_release import gh
from release_notes import previous_release
from service_control import running_images, service_state


def signed_installer(directory, target_version):
    manifest = (directory / "update-manifest.json").read_bytes()
    signature = (directory / "update-manifest.sig").read_bytes()
    public = base64.b64decode((ROOT / "assets/update-public-key.txt").read_text().strip(), validate=True)
    Ed25519PublicKey.from_public_bytes(public).verify(signature, manifest)
    document = json.loads(manifest)
    item = document["components"]["installer"]
    expected = f"WindowsMegaProxy-{target_version}-win-x64-setup.exe"
    if (document.get("preview") is not False or document.get("schemaVersion") != 1 or document.get("platform") != "win-x64"
            or document["version"] != target_version or item["file"] != expected or item["version"] != target_version):
        raise RuntimeError("Unexpected signed installer version or release format")
    return item


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("version", nargs="?", default=os.environ.get("UPDATE_VERSION"))
    parser.add_argument("--candidate", type=Path, help="Verify a signed local release before publication")
    args = parser.parse_args()
    if not args.version or not re.fullmatch(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)", args.version):
        parser.error("A published target version (X.Y.Z) is required.")
    if os.environ.get("GITHUB_ACTIONS") != "true" or not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Published update checks require an elevated disposable GitHub runner.")
    destination = known_folder(0x26) / "WindowsMegaProxy"
    data = known_folder(0x23) / "WindowsMegaProxy"
    local = known_folder(0x1c) / "WindowsMegaProxy"
    if destination.exists() or data.exists() or local.exists() or service_state("WindowsMegaProxy") is not None:
        raise RuntimeError("Refusing to change an existing MegaProxy installation or user configuration.")
    baseline = previous_release(args.version)
    if baseline is None:
        print("No complete earlier published release exists; first-release upgrade check is not applicable.")
        return
    logs = ROOT / "artifacts/update-checks"
    logs.mkdir(parents=True, exist_ok=True)
    dotnet("build", "tests/WindowsMegaProxy.UiChecks", "-c", "Release")

    def check(*arguments):
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", *arguments)

    metadata = logs / "baseline"
    metadata.mkdir(exist_ok=True)
    gh("release", "download", baseline, "--repo", "andre487/WindowsMegaProxy", "--dir", str(metadata),
       "--pattern", "update-manifest.json", "--pattern", "update-manifest.sig", "--clobber")

    previous_item = signed_installer(metadata, baseline[1:])
    previous = download(f"https://github.com/andre487/WindowsMegaProxy/releases/download/{baseline}/{previous_item['file']}",
                        ROOT / ".tools/downloads" / previous_item["file"], previous_item["sha256"])
    try:
        sp.run([str(previous), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual",
                "/MERGETASKS=automaticupdates", f"/LOG={logs / 'previous-install.log'}"], check=True, timeout=600)
        check("--update-preservation-check", "seed")
        sp.run([str(destination / "WindowsMegaProxy.Service.exe"), "--configure-mode", "service"], check=True, timeout=60)
        if args.candidate:
            item = signed_installer(args.candidate, args.version)
            setup = args.candidate / item["file"]
            if setup.stat().st_size != item["size"] or digest(setup, "sha256") != item["sha256"]:
                raise RuntimeError("Candidate installer does not match its signed manifest")
            cache = local / "Updates"
            cache.mkdir(parents=True, exist_ok=True)
            shutil.copy2(setup, cache / "setup.exe")
            (cache / "state.json").write_text(json.dumps({
                "Manifest": base64.b64encode((args.candidate / "update-manifest.json").read_bytes()).decode(),
                "Signature": base64.b64encode((args.candidate / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")
            sp.run([str(destination / "WindowsMegaProxy.Service.exe"), "--install-update", "now"], check=True, timeout=180)

            def upgraded():
                try:
                    return (product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == args.version
                            and service_state("WindowsMegaProxy") == 4 and not (destination / "update/setup.exe").exists())
                except (OSError, RuntimeError):
                    return False  # Files and service registration may be changing during installation.

            wait_until(upgraded, "The published updater did not install the signed release candidate.", 600)
            sp.run([str(destination / "WindowsMegaProxy.Service.exe"), "--check-dependencies"], check=True, timeout=60)
        else:
            check("--published-update-check", args.version)
        check("--update-preservation-check", "verify")
        assert service_state("WindowsMegaProxy") == 4, "The update must preserve a running service."
        assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}, "Update tests must never activate TUN."
        for _ in range(120):
            if not (destination / "update/setup.exe").exists() and not (local / "Updates/setup.exe").exists():
                break
            time.sleep(0.5)
        else:
            raise AssertionError("The updated installation must remove staged and cached installers.")
        print("Published automatic download, signed installer handoff, UI restart, service/settings preservation and cleanup passed; no TUN.")
    finally:
        if (local / "logs").is_dir():
            shutil.copytree(local / "logs", logs / "client", dirs_exist_ok=True)
        temporary = known_folder(0x1c) / "Temp"
        for log in temporary.glob("Setup Log*.txt"):
            shutil.copy2(log, logs / log.name)
        uninstaller = destination / "unins000.exe"
        if uninstaller.is_file():
            sp.run([str(uninstaller), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'uninstall.log'}"],
                   check=True, timeout=120)


if __name__ == "__main__":
    cli(main)
