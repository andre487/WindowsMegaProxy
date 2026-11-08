"""Exercise a published automatic update only on an empty GitHub-hosted Windows runner."""
import argparse
import ctypes as ct
import os
import re
import shutil
import subprocess as sp
import time

from common import ROOT, cli, dotnet, download
from install import known_folder
from service_control import running_images, service_state


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("version", nargs="?", default=os.environ.get("UPDATE_VERSION"))
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
    logs = ROOT / "artifacts/update-checks"
    logs.mkdir(parents=True, exist_ok=True)
    dotnet("build", "tests/WindowsMegaProxy.UiChecks", "-c", "Release")

    def check(*arguments):
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", *arguments)

    previous = download("https://github.com/andre487/WindowsMegaProxy/releases/download/v0.0.8/WindowsMegaProxy-0.0.8-win-x64-setup.exe",
                        ROOT / ".tools/downloads/WindowsMegaProxy-0.0.8-win-x64-setup.exe",
                        "6335d9d644a35961110bf0c944af487303a5d432d6d41ffb396af13dbbe4fff5")
    try:
        sp.run([str(previous), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual",
                "/MERGETASKS=automaticupdates", f"/LOG={logs / 'previous-install.log'}"], check=True, timeout=600)
        check("--update-preservation-check", "seed")
        sp.run([str(destination / "WindowsMegaProxy.Service.exe"), "--configure-mode", "service"], check=True, timeout=60)
        check("--published-update-check", args.version)
        check("--update-preservation-check", "verify")
        assert service_state("WindowsMegaProxy") == 4, "The update must preserve a running service."
        assert "sing-box.exe" not in running_images(), "Update tests must never activate TUN."
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
