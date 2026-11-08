"""Install/repair/uninstall only on an empty disposable GitHub-hosted runner."""
import csv
import ctypes as ct
import json
import os
import re
import subprocess as sp
import time
import winreg
import xml.etree.ElementTree as ET
from pathlib import Path

from common import ROOT, cli, digest, dotnet
from install import known_folder
from run import stop_workspace_app, workspace_windows
from service_control import running_images, service_state


def check_downloaded_cores(version_directory):
    manifest = json.loads((ROOT / "artifacts/release-preview/update-manifest.json").read_text(encoding="utf-8"))
    for name in ("sing-box", "sing-box-h3"):
        for entry in manifest["components"][name]["files"]:
            path = version_directory / entry["path"]
            assert path.is_file() and digest(path, "sha256") == entry["sha256"], "Downloaded core differs from the verified official package."
    snapshot = version_directory / "installer-state/rollback.json"
    acl_file = ROOT / "artifacts/installer-checks" / (version_directory.name + "-acl.txt")
    sp.run(["icacls.exe", str(snapshot), "/save", str(acl_file), "/q"], check=True, capture_output=True)
    descriptor = acl_file.read_text(encoding="utf-16")
    allowed = set(re.findall(r"\(A;[^)]*;;;([^)]*)\)", descriptor))
    assert allowed == {"BA", "SY"}, "Rollback configuration snapshots must be readable only by administrators and SYSTEM."


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
        arguments = [str(setups[0]), "/install" if attempt == 1 else "/repair", "/quiet", "/norestart", "/permachine", "InstallSSH=0", "AutomaticUpdates=false", "/log", str(logs / f"install-{attempt}.log")]
        if attempt != 1:
            arguments.append("Mode=" + mode)
        sp.run(arguments, check=True, timeout=600)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
            assert winreg.QueryValueEx(key, "Mode")[0] == mode
            current = winreg.QueryValueEx(key, "HelperPath")[0]
        check_downloaded_cores(Path(current).parent)
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
                failure = sp.run([str(setups[0]), "/repair", "/quiet", "/norestart", "/permachine", "InstallSSH=0", "Mode=manual", "/log", str(logs / "mode-change-failed.log")], timeout=120)
                assert failure.returncode != 0, "A failed mode configuration must fail the installer."
                assert service_state("WindowsMegaProxy") == 4, "A denied mode change must preserve the running service."
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
                    assert winreg.QueryValueEx(key, "Mode")[0] == "service", "A failed mode change must preserve the previous mode."
            finally:
                sp.run(["sc.exe", "sdset", "WindowsMegaProxy", original], check=True, capture_output=True)
        previous = current
        time.sleep(1)
    check_user_install(setups[0], logs, data)
    sp.run([str(setups[0]), "/uninstall", "/quiet", "/norestart", "/permachine", "/log", str(logs / "uninstall.log")], check=True, timeout=120)
    assert service_state("WindowsMegaProxy") is None, "Uninstall must remove its service."
    assert (data / "preservation-check.txt").read_text() == "preserved", "Uninstall must preserve global data."
    print("Manual/login/service mode transitions, encrypted settings preservation, repair and uninstall passed; TUN was not started.")


def check_user_install(setup, logs, global_data):
    sid = next(csv.reader(sp.check_output(["whoami.exe", "/user", "/fo", "csv", "/nh"], text=True).splitlines()))[1]
    local = known_folder(0x1c)
    destination = local / "Programs/WindowsMegaProxy"
    protected = known_folder(0x26) / "WindowsMegaProxy.Users" / sid
    if destination.exists() or protected.exists():
        raise RuntimeError("Refusing to alter an existing current-user installation.")
    namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
    name = "WindowsMegaProxy.User." + sid
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
        system_helper = winreg.QueryValueEx(key, "HelperPath")[0]
    previous = None
    for mode in ("manual", "login", "manual"):
        sp.run([str(setup), "/repair" if previous else "/install", "/quiet", "/norestart", "/peruser", "InstallSSH=0", "AutomaticUpdates=false", "Mode=" + mode,
                "/log", str(logs / f"user-{mode}.log")], check=True, timeout=600)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\WindowsMegaProxy\UserInstallation") as key:
            assert winreg.QueryValueEx(key, "Mode")[0] == mode
            helper = winreg.QueryValueEx(key, "HelperPath")[0]
        check_downloaded_cores(Path(helper).parent)
        assert str(protected / "versions").casefold() in helper.casefold()
        assert (destination / "WindowsMegaProxy.Launcher.exe").is_file()
        assert not (destination / "WindowsMegaProxy.exe").exists(), "Only the unprivileged launcher belongs in the user-writable install directory."
        assert (global_data / "preservation-check.txt").read_text() == "preserved"
        assert service_state("WindowsMegaProxy") is None
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
            assert winreg.QueryValueEx(key, "HelperPath")[0] == system_helper, "A user install must preserve the system registration."
        for task_name, login in ((name, False), (name + ".Login", True)):
            task = sp.run(["schtasks.exe", "/Query", "/TN", task_name, "/XML"], capture_output=True, text=True)
            if login and mode != "login":
                assert task.returncode != 0
                continue
            assert task.returncode == 0
            document = ET.fromstring(task.stdout)
            assert document.findtext("t:Principals/t:Principal/t:UserId", namespaces=namespace) == sid
            assert document.findtext("t:Principals/t:Principal/t:RunLevel", namespaces=namespace) == "HighestAvailable"
            assert document.findtext("t:Principals/t:Principal/t:LogonType", namespaces=namespace) == "InteractiveToken"
            assert (document.find("t:Triggers/t:LogonTrigger", namespace) is not None) == login
            assert document.findtext("t:Actions/t:Exec/t:Command", namespaces=namespace) == str(Path(helper).with_name("WindowsMegaProxy.exe"))
        dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "--", "--user-settings-check", "verify" if previous else "seed")
        if previous is None:
            sp.run([str(destination / "WindowsMegaProxy.Launcher.exe")], check=True, timeout=30)
            for _ in range(150):
                if "windowsmegaproxy.exe" in running_images() and workspace_windows("MegaProxy"):
                    break
                time.sleep(0.2)
            else:
                raise RuntimeError("The protected scheduled user application did not open its window.")
            sp.run([helper, "--set-mode", "login"], check=True, timeout=60)
            for _ in range(150):
                if "windowsmegaproxy.exe" in running_images() and workspace_windows("MegaProxy"):
                    break
                time.sleep(0.2)
            else:
                raise RuntimeError("Changing the user operating mode did not relaunch the scheduled application.")
            stop_workspace_app(dev=False, workspace_only=False)
            assert "sing-box.exe" not in running_images(), "The fixture must never activate TUN."
        previous = helper
    rejected = sp.run([str(setup), "/repair", "/quiet", "/norestart", "/peruser", "InstallSSH=0", "Mode=service",
                       "/log", str(logs / "user-service-rejected.log")], timeout=120)
    assert rejected.returncode != 0, "A current-user install must reject service mode."
    sp.run([str(setup), "/uninstall", "/quiet", "/norestart", "/peruser", "/log", str(logs / "user-uninstall.log")], check=True, timeout=120)
    for task_name in (name, name + ".Login"):
        assert sp.run(["schtasks.exe", "/Query", "/TN", task_name], capture_output=True).returncode != 0
    assert (local / "WindowsMegaProxy/Personal/settings.json").is_file(), "Personal settings must survive uninstall."
    assert not list(protected.rglob("user-installation.sid")), "Uninstall must remove markers from every installed version."
    assert (global_data / "preservation-check.txt").read_text() == "preserved"
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
        assert winreg.QueryValueEx(key, "HelperPath")[0] == system_helper, "User uninstall must preserve the system registration."
    print("Current-user install/repair, protected highest-privilege tasks, personal DPAPI settings and uninstall passed.")


if __name__ == "__main__":
    cli(main)
