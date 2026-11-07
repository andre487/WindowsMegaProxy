"""Exercise explicit DEV modes and complete cleanup on a disposable runner."""
import ctypes as ct
import os
import subprocess as sp
import time
import winreg

from common import ROOT, cli, dotnet
from install import known_folder
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
            assert str(root / "versions").casefold() in helper.casefold()
            assert service_state(NAME) == (4 if mode == "service" else None)
            tasks, _ = startup_records()
            assert bool(tasks) == (mode == "login")
            dotnet("run", "--project", str(ROOT / "tests/WindowsMegaProxy.UiChecks"), "-c", "Release", "-p:MegaProxyDev=true", "--no-build", "--", "--installed-settings-check", "seed" if mode == "manual" else "verify")
            assert "sing-box-dev.exe" not in running_images(), "Unconfigured DEV must never start TUN."
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
                assert "sing-box-dev.exe" not in running_images(), "Restarting the seeded UI must not start TUN."
                stop_workspace_app(dev=True, workspace_only=False)
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


if __name__ == "__main__":
    cli(main)
