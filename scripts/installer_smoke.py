"""Install/repair/uninstall only on an empty disposable GitHub-hosted runner."""
import argparse
import csv
import ctypes as ct
import json
import os
import shutil
import subprocess as sp
import time
import winreg
import xml.etree.ElementTree as ET
from pathlib import Path

from common import ROOT, cli, digest, dotnet, download
from deferred_update_smoke import check as check_deferred_updates
from install import known_folder
from run import stop_workspace_app, user32, workspace_windows
from service_control import running_images, service_state


def seed_legacy_cores(directory):
    for folder in ("sing-box", "sing-box-h3"):
        for name in ("sing-box.exe", "sing-box-dev.exe", "libcronet.dll"):
            (directory / "dependencies" / folder / name).write_bytes(b"obsolete core fixture")


def check_downloaded_cores(version_directory):
    manifest = json.loads((ROOT / "artifacts/release-preview/update-manifest.json").read_text(encoding="utf-8"))
    for name in ("sing-box", "sing-box-h3"):
        for legacy in ("sing-box.exe", "sing-box-dev.exe", "libcronet.dll"):
            assert not (version_directory / "dependencies" / name / legacy).exists(), "Legacy core files must be removed."
        assert (version_directory / "dependencies" / name / "WindowsMegaProxy.Core.exe").is_file(), "Both pinned cores must be bundled."
        library = "MegaProxy.Core.Stable.dll" if name == "sing-box" else "MegaProxy.Core.Experimental.dll"
        assert (version_directory / "dependencies" / name / library).is_file()
        for entry in manifest["components"][name]["files"]:
            assert digest(version_directory / entry["path"], "sha256") == entry["sha256"], "Bundled core must match the pinned release files."
    assert not (version_directory / "WindowsMegaProxy.Http3Core.exe").exists(), "No on-demand installer is needed."
    assert (version_directory / "WindowsMegaProxy.runtimeconfig.json").is_file(), "Production must use a self-contained folder."
    assert (version_directory / "coreclr.dll").is_file(), "The pinned .NET runtime must be bundled."
    environment = os.environ.copy()
    environment["DOTNET_ROOT"] = str(ROOT / "artifacts/nonexistent-dotnet")
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(ROOT / "artifacts/installer-checks/runtime-cache" / version_directory.name)
    result = sp.run([str(version_directory / "WindowsMegaProxy.Service.exe"), "--check-dependencies"], check=True, timeout=30,
                    env=environment, capture_output=True, text=True)
    expected_runtime = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/RuntimeFrameworkVersion")
    assert ".NET: " + expected_runtime in result.stdout
    assert str(version_directory / "coreclr.dll").casefold() in result.stdout.casefold(), "The process must load its bundled runtime, not an installed system copy."


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--suite", choices=("all", "installation", "updates"), default="all")
    suite = parser.parse_args().suite
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
    if suite == "updates":
        check_deferred_updates(setups[0], logs, destination, data, fresh=True)
        return

    def service_path():
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy") as key:
            return winreg.QueryValueEx(key, "ImagePath")[0]

    previous = None
    for attempt, mode in enumerate(("manual", "login", "service", "service", "manual"), 1):
        if previous:
            seed_legacy_cores(destination)
        arguments = [str(setups[0]), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", f"/LOG={logs / f'install-{attempt}.log'}"]
        if attempt != 1:
            arguments.append("/MODE=" + mode)
        sp.run(arguments, check=True, timeout=600)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
            assert winreg.QueryValueEx(key, "Mode")[0] == mode
            current = winreg.QueryValueEx(key, "HelperPath")[0]
        check_downloaded_cores(Path(current).parent)
        assert service_state("WindowsMegaProxy") == (4 if mode == "service" else None), "Only service mode may register a service."
        if mode == "service":
            assert service_path().strip('"') == current
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy") as key:
                try:
                    environment = winreg.QueryValueEx(key, "Environment")[0]
                except FileNotFoundError:
                    environment = []
                assert not any(value.startswith("DOTNET_BUNDLE_EXTRACT_BASE_DIR=") for value in environment), "Folder builds must not extract .NET at startup."
        task = sp.run(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Login", "/XML"], capture_output=True, text=True)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run") as startup:
            try:
                tray_startup = winreg.QueryValueEx(startup, "WindowsMegaProxy")[0]
            except FileNotFoundError:
                tray_startup = None
        expected_tray = f'"{destination / "WindowsMegaProxy.exe"}" --tray' if mode == "service" else None
        assert tray_startup == expected_tray, "Service mode must start a non-elevated tray client for each user; other modes must remove it."
        if mode == "login":
            assert task.returncode == 0, "Login mode must register its task."
            document = ET.fromstring(task.stdout)
            namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
            assert document.findtext("t:Principals/t:Principal/t:RunLevel", namespaces=namespace) == "HighestAvailable"
            assert document.findtext("t:Principals/t:Principal/t:LogonType", namespaces=namespace) == "InteractiveToken"
            assert document.find("t:Triggers/t:LogonTrigger", namespace) is not None
        else:
            assert task.returncode != 0, "Other modes must remove the login task."
        tray_task = sp.run(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Tray", "/XML"], capture_output=True, text=True)
        assert (tray_task.returncode == 0) == (mode == "service")
        if mode == "service":
            document = ET.fromstring(tray_task.stdout)
            namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
            assert document.findtext("t:Principals/t:Principal/t:RunLevel", default="LeastPrivilege", namespaces=namespace) == "LeastPrivilege"
            assert document.findtext("t:Principals/t:Principal/t:GroupId", namespaces=namespace) == "S-1-5-32-545"
        assert Path(current).parent == destination, "Service must use the fixed protected directory."
        assert not (destination / "versions").exists()
        if previous:
            assert current == previous, "Repair must keep the same installation path after stopping the service."
            assert (data / "preservation-check.txt").read_text() == "preserved", "Upgrade must preserve global data."
        else:
            (data / "preservation-check.txt").write_text("preserved")
        dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "--no-build", "--", "--installed-settings-check", "seed" if attempt == 1 else "verify")
        assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}, "An unconfigured installation must not activate TUN."
        assert service_state("WindowsMegaProxy.Dev") is None, "Installer must not register DEV."
        if attempt == 3:
            check_service_tray(destination)
            check_failed_update_helper(Path(current), logs)
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
    check_user_install(setups[0], logs, data)
    sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'uninstall.log'}"], check=True, timeout=120)
    assert service_state("WindowsMegaProxy") is None, "Uninstall must remove its service."
    assert sp.run(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Tray"], capture_output=True).returncode != 0
    assert (data / "preservation-check.txt").read_text() == "preserved", "Uninstall must preserve global data."
    check_legacy_upgrade(setups[0], logs, destination, data)
    if suite == "all":
        check_deferred_updates(setups[0], logs, destination, data)
    print("Manual/login/service mode transitions, encrypted settings preservation, repair and uninstall passed; TUN was not started.")


def check_service_tray(destination):
    user32.IsWindowVisible.argtypes = [ct.c_void_p]
    client = sp.Popen([str(destination / "WindowsMegaProxy.exe"), "--tray"])
    try:
        for _ in range(120):
            windows = workspace_windows("MegaProxy")
            if windows and not user32.IsWindowVisible(windows[0][0]):
                break
            assert client.poll() is None, "The tray client must remain running."
            time.sleep(0.25)
        else:
            raise RuntimeError("The service client did not start hidden in the tray.")
        sp.run([str(destination / "WindowsMegaProxy.exe")], check=True, timeout=30)
        for _ in range(120):
            windows = workspace_windows("MegaProxy")
            if windows and user32.IsWindowVisible(windows[0][0]):
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("An explicit launch did not restore the tray client.")
        assert client.poll() is None and len(windows) == 1 and windows[0][1] == client.pid
    finally:
        stop_workspace_app(dev=False, workspace_only=False)
        client.wait(timeout=30)
    assert service_state("WindowsMegaProxy") == 4, "Closing the tray client must leave the service running."
    assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}, "The seeded tray client must not activate TUN."


def check_failed_update_helper(helper, logs):
    cache = known_folder(0x1c) / "WindowsMegaProxy/Updates"
    assert not (cache / "state.json").exists(), "The update failure fixture requires an empty disposable user cache."
    staging = helper.parent / "update"
    staging.mkdir(exist_ok=True)
    target = staging / "setup.exe"
    marker = b"previous staged installer must survive an early update failure"
    target.write_bytes(marker)
    try:
        for _ in range(2):
            result = sp.run([str(helper), "--install-update"], capture_output=True, text=True, timeout=20)
            assert result.returncode == 1, "A failed update must exit instead of keeping installation DLLs locked in a hidden dialog."
            assert target.read_bytes() == marker, "An early failure must not delete an installer it did not copy."
        log = known_folder(0x1c) / "WindowsMegaProxy/logs/current.log"
        assert "Application update installation failed:" in log.read_text(encoding="utf-8")
        (logs / "update-helper-failed.txt").write_text(result.stderr, encoding="utf-8")
    finally:
        target.unlink(missing_ok=True)


def check_legacy_upgrade(setup, logs, destination, data):
    previous = download("https://github.com/andre487/WindowsMegaProxy/releases/download/v0.0.4/WindowsMegaProxy-0.0.4-win-x64-setup.exe",
                        ROOT / ".tools/downloads/WindowsMegaProxy-0.0.4-win-x64-setup.exe",
                        "3e1651d131b6f64d996533fc26d6018ad44f5e6df3202d66ff37c8a39b0bee13")
    sp.run([str(previous), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/MODE=service",
            f"/LOG={logs / 'legacy-install.log'}"], check=True, timeout=600)
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
        helper = winreg.QueryValueEx(key, "HelperPath")[0]
    assert Path(helper).parent.parent == destination / "versions"
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ct.c_wchar_p, ct.c_ulong, ct.c_ulong, ct.c_void_p, ct.c_ulong, ct.c_ulong, ct.c_void_p]
    kernel.CreateFileW.restype = ct.c_void_p
    kernel.CloseHandle.argtypes = [ct.c_void_p]
    # Emulate 0.0.4's updater keeping the old service EXE locked until setup exits.
    locked = kernel.CreateFileW(helper, 0x80000000, 1, None, 3, 0, None)
    if locked == ct.c_void_p(-1).value:
        raise ct.WinError(ct.get_last_error())
    try:
        sp.run([str(setup), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/UPDATE=1",
                f"/LOG={logs / 'legacy-upgrade.log'}"], check=True, timeout=600)
    finally:
        kernel.CloseHandle(locked)
    for _ in range(120):
        if not (destination / "versions").exists():
            break
        time.sleep(0.5)
    client_log = known_folder(0x1c) / "WindowsMegaProxy/logs/current.log"
    if client_log.is_file():
        shutil.copy2(client_log, logs / "legacy-upgrade-client.log")
    assert not (destination / "versions").exists(), "Upgrade must remove legacy versions after the old updater exits."
    for _ in range(120):
        if workspace_windows("MegaProxy"):
            break
        time.sleep(0.25)
    assert workspace_windows("MegaProxy"), "Automatic updates must reopen the new application."
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as key:
        assert Path(winreg.QueryValueEx(key, "HelperPath")[0]).parent == destination
        assert winreg.QueryValueEx(key, "Mode")[0] == "service"
    assert service_state("WindowsMegaProxy") == 4
    assert (data / "preservation-check.txt").read_text() == "preserved"
    check_downloaded_cores(destination)
    dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "--no-build", "--", "--installed-settings-check", "verify")
    assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}
    sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            f"/LOG={logs / 'legacy-upgrade-uninstall.log'}"], check=True, timeout=120)
    assert service_state("WindowsMegaProxy") is None
    print("Official 0.0.4 upgrade, locked old updater, fixed paths, settings preservation and legacy cleanup passed; no TUN.")


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
        if previous:
            seed_legacy_cores(protected)
        sp.run([str(setup), "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/MODE=" + mode,
                f"/LOG={logs / f'user-{mode}.log'}"], check=True, timeout=600)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\WindowsMegaProxy\UserInstallation") as key:
            assert winreg.QueryValueEx(key, "Mode")[0] == mode
            helper = winreg.QueryValueEx(key, "HelperPath")[0]
        check_downloaded_cores(Path(helper).parent)
        assert Path(helper).parent == protected
        assert not (protected / "versions").exists()
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
        dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "--no-build", "--", "--user-settings-check", "verify" if previous else "seed")
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
            assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}, "The fixture must never activate TUN."
        previous = helper
    rejected = sp.run([str(setup), "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/MODE=service",
                       f"/LOG={logs / 'user-service-rejected.log'}"], timeout=120)
    assert rejected.returncode != 0, "A current-user install must reject service mode."
    sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'user-uninstall.log'}"], check=True, timeout=120)
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
