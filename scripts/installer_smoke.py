"""Install/repair/uninstall only on an empty disposable GitHub-hosted runner."""
import ctypes as ct
import os
import subprocess as sp
import time
import winreg
import xml.etree.ElementTree as ET

from common import ROOT, cli, dotnet
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
    for attempt, mode in enumerate(("manual", "login", "service", "service", "manual"), 1):
        arguments = [str(setups[0]), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", f"/LOG={logs / f'install-{attempt}.log'}"]
        if attempt != 1:
            arguments.append("/MODE=" + mode)
        sp.run(arguments, check=True, timeout=600)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
            assert winreg.QueryValueEx(key, "Mode")[0] == mode
            current = winreg.QueryValueEx(key, "HelperPath")[0]
        assert service_state("WindowsMegaProxy") == (4 if mode == "service" else None), "Only service mode may register a service."
        if mode == "service":
            assert service_path().strip('"') == current
        task = sp.run(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Login", "/XML"], capture_output=True, text=True)
        if mode == "login":
            assert task.returncode == 0, "Login mode must register its task."
            document = ET.fromstring(task.stdout)
            namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
            assert document.findtext("t:Principals/t:Principal/t:RunLevel", namespaces=namespace) == "HighestAvailable"
            assert document.findtext("t:Principals/t:Principal/t:LogonType", namespaces=namespace) == "InteractiveToken"
            assert document.find("t:Triggers/t:LogonTrigger", namespace) is not None
        else:
            assert task.returncode != 0, "Other modes must remove the login task."
        assert str(destination / "versions").casefold() in current.casefold(), "Service must use a protected version directory."
        if previous:
            assert current != previous, "Repair must not overwrite loaded binaries."
            assert (data / "preservation-check.txt").read_text() == "preserved", "Upgrade must preserve global data."
        else:
            (data / "preservation-check.txt").write_text("preserved")
        dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "--no-build", "--", "--installed-settings-check", "seed" if attempt == 1 else "verify")
        assert "sing-box.exe" not in running_images(), "An unconfigured installation must not activate TUN."
        assert service_state("WindowsMegaProxy.Dev") is None, "Installer must not register DEV."
        if attempt == 3:
            security = sp.check_output(["sc.exe", "sdshow", "WindowsMegaProxy"], text=True)
            original = next(line.strip() for line in security.splitlines() if line.strip().startswith("D:"))
            first_entry = original.index("(")
            denied = original[:first_entry] + "(D;;WP;;;BA)" + original[first_entry:]
            sp.run(["sc.exe", "sdset", "WindowsMegaProxy", denied], check=True, capture_output=True)
            try:
                failure = sp.run([str(setups[0]), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/MODE=manual", f"/LOG={logs / 'mode-change-failed.log'}"], timeout=120)
                assert failure.returncode != 0, "A failed mode configuration must fail the installer."
                assert service_state("WindowsMegaProxy") == 4, "A denied mode change must preserve the running service."
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
                    assert winreg.QueryValueEx(key, "Mode")[0] == "service", "A failed mode change must preserve the previous mode."
            finally:
                sp.run(["sc.exe", "sdset", "WindowsMegaProxy", original], check=True, capture_output=True)
        previous = current
        time.sleep(1)
    sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'uninstall.log'}"], check=True, timeout=120)
    assert service_state("WindowsMegaProxy") is None, "Uninstall must remove its service."
    assert (data / "preservation-check.txt").read_text() == "preserved", "Uninstall must preserve global data."
    print("Manual/login/service mode transitions, encrypted settings preservation, repair and uninstall passed; TUN was not started.")


if __name__ == "__main__":
    cli(main)
