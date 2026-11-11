"""Exercise explicit DEV modes and complete cleanup on a disposable runner."""
import ctypes as ct
import os
import shutil
import subprocess as sp
import tempfile
import time
import winreg
from pathlib import Path

from common import ROOT, cli, dotnet
from install import known_folder
from installer_smoke_support import uninstall
from run import stop_workspace_app, workspace_windows
from service_control import running_images, service_state, system_executable
from uninstall_dev import NAME, RUN_KEY, startup_records


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted" or not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("DEV mode smoke tests require an elevated disposable GitHub runner.")
    root = known_folder(0x26) / NAME
    data = known_folder(0x23) / NAME
    if root.exists() or data.exists() or service_state(NAME) is not None or any(startup_records()):
        raise RuntimeError("Refusing to alter an existing DEV installation.")
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        winreg.SetValueEx(key, "WindowsMegaProxy", 0, winreg.REG_SZ, "production-sentinel")
    try:
        for mode in ("manual", "login", "service"):
            sp.run([str(ROOT / "scripts/.venv/Scripts/python.exe"), str(ROOT / "scripts/install.py"), "--dev", "--mode", mode], check=True, timeout=600)
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, "SOFTWARE\\" + NAME) as key:
                assert winreg.QueryValueEx(key, "Mode")[0] == mode
                helper = winreg.QueryValueEx(key, "HelperPath")[0]
            assert Path(helper).parent == root
            assert not (root / "versions").exists()
            assert service_state(NAME) == (4 if mode == "service" else None)
            tasks, _ = startup_records()
            assert bool(tasks) == (mode in ("login", "service"))
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, RUN_KEY) as startup:
                try:
                    tray_startup = winreg.QueryValueEx(startup, NAME)[0]
                except FileNotFoundError:
                    tray_startup = None
            assert tray_startup == (f'"{root / (NAME + ".exe")}" --tray' if mode == "service" else None)
            dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "-p:MegaProxyDev=true", "--no-build", "--", "--installed-settings-check", "seed" if mode == "manual" else "verify")
            assert not running_images() & {"sing-box-dev.exe", "WindowsMegaProxy.Dev.Core.exe"}, "Unconfigured DEV must never start TUN."
            if mode == "login":
                sp.run([str(ROOT / "scripts/.venv/Scripts/python.exe"), str(ROOT / "scripts/run.py"), "--elevated"], check=True, timeout=600)
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, "SOFTWARE\\" + NAME) as key:
                    assert winreg.QueryValueEx(key, "Mode")[0] == "login", "run.cmd must preserve the selected DEV mode."
                for _ in range(120):
                    if "windowsmegaproxy.dev.exe" in running_images() and workspace_windows():
                        break
                    time.sleep(0.25)
                else:
                    raise RuntimeError("DEV did not reopen after configuring sign-in mode.")
                assert not running_images() & {"sing-box-dev.exe", "WindowsMegaProxy.Dev.Core.exe"}, "Restarting the seeded UI must not start TUN."
                stop_workspace_app(dev=True, workspace_only=False)
        check_channel_switch(root)
        sp.run([system_executable("schtasks.exe"), "/Create", "/TN", NAME + "-S-1-5-18", "/SC", "ONSTART", "/RU", "SYSTEM", "/TR", "cmd.exe /c exit 0", "/F"], check=True)
        for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
            for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
                with winreg.CreateKeyEx(hive, RUN_KEY, 0, winreg.KEY_SET_VALUE | view) as key:
                    winreg.SetValueEx(key, NAME, 0, winreg.REG_SZ, "dev-sentinel")
        sp.run([str(ROOT / "scripts/.venv/Scripts/python.exe"), str(ROOT / "scripts/uninstall_dev.py")], check=True, timeout=120)
        assert service_state(NAME) is None and not any(startup_records()) and not root.exists()
        assert (data / "settings.json").is_file(), "Cleanup must preserve encrypted DEV settings."
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            assert winreg.QueryValueEx(key, "WindowsMegaProxy")[0] == "production-sentinel"
        sp.run([str(ROOT / "scripts/.venv/Scripts/python.exe"), str(ROOT / "scripts/uninstall_dev.py")], check=True, timeout=60)
        print("All DEV modes, complete/idempotent startup cleanup and production isolation passed; no TUN.")
    finally:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
            winreg.DeleteValue(key, "WindowsMegaProxy")


def check_channel_switch(dev_root):
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Channel switch checks are restricted to disposable GitHub runners.")
    setup = next((ROOT / "artifacts/release-preview").glob("*-setup.exe"))
    production = known_folder(0x26) / "WindowsMegaProxy"
    assert service_state("WindowsMegaProxy") is None
    source = ROOT / "tests/WindowsMegaProxy.UiChecks/bin/dev/Release/net10.0-windows"
    environment = os.environ.copy()
    environment["DOTNET_ROOT"] = str(ROOT / ".tools/dotnet")
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"

    def start_service(name):
        sp.run([system_executable("sc.exe"), "start", name], check=True, capture_output=True)
        for _ in range(100):
            if service_state(name) == 4:
                return
            time.sleep(0.1)
        raise RuntimeError("The test service did not start.")

    with tempfile.TemporaryDirectory(prefix="channel-fixture-", dir=ROOT / "artifacts") as temporary:
        directory = Path(temporary)
        shutil.copytree(source, directory, dirs_exist_ok=True)

        def start_window(name):
            executable = directory / (name + ".exe")
            shutil.copy2(source / "WindowsMegaProxy.UiChecks.exe", executable)
            ready = directory / (name + ".ready")
            process = sp.Popen([str(executable), "--channel-fixture", str(ready)], env=environment)
            for _ in range(100):
                if ready.is_file():
                    return process
                if process.poll() is not None:
                    break
                time.sleep(0.1)
            process.kill()
            process.wait()
            raise RuntimeError("The isolated channel window did not open.")

        fixture = start_window("WindowsMegaProxy")
        try:
            sp.run([str(setup), "Scope=PerMachine", "-quiet", "-norestart", "Mode=service", "AutomaticUpdates=false", "DesktopShortcut=0"], check=True, timeout=600)
            dev_ui = dev_root / "WindowsMegaProxy.Dev.exe"
            prod_ui = production / "WindowsMegaProxy.exe"
            sp.run([str(dev_ui), "--resolve-channel-conflict", "exit-current"], check=True, timeout=60)
            assert service_state(NAME) == 1 and service_state("WindowsMegaProxy") == 4 and fixture.poll() is None
            start_service(NAME)
            sp.run([str(dev_ui), "--resolve-channel-conflict", "stop-other"], check=True, timeout=60)
            assert fixture.wait(timeout=10) == 0
            assert service_state("WindowsMegaProxy") == 1 and service_state(NAME) == 4
            start_service("WindowsMegaProxy")
            fixture = start_window(NAME)
            for _ in range(2):
                sp.run([str(prod_ui), "--resolve-channel-conflict", "stop-other"], check=True, timeout=60)
                assert service_state(NAME) == 1 and service_state("WindowsMegaProxy") == 4
            assert fixture.wait(timeout=10) == 0
            original = sp.Popen([str(prod_ui), "--tray"])
            try:
                for _ in range(100):
                    if any(pid == original.pid for _, pid in workspace_windows("MegaProxy")):
                        break
                    time.sleep(0.1)
                sp.run([str(prod_ui)], check=True, timeout=30)
                user = ct.WinDLL("user32", use_last_error=True)
                user.IsWindowVisible.argtypes = [ct.c_void_p]
                for _ in range(100):
                    windows = workspace_windows("MegaProxy")
                    if any(pid == original.pid and user.IsWindowVisible(window) for window, pid in windows):
                        break
                    time.sleep(0.1)
                assert len(windows) == 1 and windows[0][1] == original.pid and user.IsWindowVisible(windows[0][0])
                assert original.poll() is None, "A repeated launch must restore the original window, not replace it."
            finally:
                stop_workspace_app(dev=False, workspace_only=False)
                original.wait(timeout=30)
            assert not running_images() & {"sing-box.exe", "sing-box-dev.exe", "WindowsMegaProxy.Core.exe", "WindowsMegaProxy.Dev.Core.exe"}
            print("Production/DEV channel switching, exit-current, graceful window close and idempotent target state passed; no TUN.")
        finally:
            if fixture.poll() is None:
                fixture.kill()
                fixture.wait()
            uninstall(ROOT / "artifacts/installer-checks/dev-production-uninstall.log")


if __name__ == "__main__":
    cli(main)
