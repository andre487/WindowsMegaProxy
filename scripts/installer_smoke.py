"""Install/repair/uninstall only on an empty disposable GitHub-hosted runner."""
import ctypes as ct
import os
import subprocess as sp
import time
import winreg

from common import ROOT, cli
from install import known_folder
from service_control import running_images, service_state


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Installer smoke tests are restricted to disposable GitHub-hosted runners.")
    if not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Installer smoke tests need an elevated runner.")
    destination = known_folder(0x26) / "WindowsMegaProxy"
    data = known_folder(0x23) / "WindowsMegaProxy"
    if destination.exists() or data.exists() or service_state("WindowsMegaProxy") is not None:
        raise RuntimeError("Refusing to alter an existing MegaProxy installation.")
    setups = list((ROOT / "artifacts/release-preview").glob("*-setup.exe"))
    if len(setups) != 1:
        raise RuntimeError("Expected exactly one preview installer.")
    logs = ROOT / "artifacts/installer-checks"
    logs.mkdir(parents=True, exist_ok=True)

    def service_path():
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy") as key:
            return winreg.QueryValueEx(key, "ImagePath")[0]

    previous = None
    for attempt in (1, 2):
        sp.run([str(setups[0]), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", f"/LOG={logs / f'install-{attempt}.log'}"], check=True, timeout=600)
        assert service_state("WindowsMegaProxy") == 4, "Installed service must be running."
        current = service_path()
        assert str(destination / "versions").casefold() in current.casefold(), "Service must use a protected version directory."
        if previous:
            assert current != previous, "Repair must not overwrite loaded binaries."
            assert (data / "preservation-check.txt").read_text() == "preserved", "Upgrade must preserve global data."
        else:
            (data / "preservation-check.txt").write_text("preserved")
        assert "sing-box.exe" not in running_images(), "An unconfigured installation must not activate TUN."
        assert service_state("WindowsMegaProxy.Dev") is None, "Installer must not register DEV."
        previous = current
        time.sleep(1)
    sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'uninstall.log'}"], check=True, timeout=120)
    assert service_state("WindowsMegaProxy") is None, "Uninstall must remove its service."
    assert (data / "preservation-check.txt").read_text() == "preserved", "Uninstall must preserve global data."
    print("Native installer install/repair/uninstall checks passed; TUN was not started.")


if __name__ == "__main__":
    cli(main)
