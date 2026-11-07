"""Rebuild and restart DEV while preserving its selected operating mode."""

import os
import ctypes as ct
from ctypes import wintypes as wt
from pathlib import Path
import subprocess as sp
import sys
import shutil
import winreg

from common import ROOT, cli, artifact_root, app_name
from service_control import require_production_stopped, stop_service, service_state
from install import elevate, elevated_cli
import build

user32 = ct.WinDLL("user32", use_last_error=True)
kernel32 = ct.WinDLL("kernel32", use_last_error=True)
callback_type = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [callback_type, wt.LPARAM]
user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ct.c_int]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ct.c_int]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ct.POINTER(wt.DWORD)]
user32.SendMessageTimeoutW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM, wt.UINT, wt.UINT, ct.POINTER(ct.c_size_t)]
user32.SendMessageTimeoutW.restype = ct.c_size_t
kernel32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
kernel32.OpenProcess.restype = wt.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ct.POINTER(wt.DWORD)]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.CloseHandle.argtypes = [wt.HANDLE]
kernel32.GetExitCodeProcess.argtypes = [wt.HANDLE, ct.POINTER(wt.DWORD)]


def workspace_windows(title="MegaProxy DEV"):
    windows = []

    @callback_type
    def collect(window, _):
        text, window_class = ct.create_unicode_buffer(256), ct.create_unicode_buffer(256)
        user32.GetWindowTextW(window, text, 256)
        user32.GetClassNameW(window, window_class, 256)
        if text.value == title and window_class.value.startswith("HwndWrapper"):
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(window, ct.byref(pid))
            windows.append((window, pid.value))
        return True

    user32.EnumWindows(collect, 0)
    return windows


def stop_workspace_app(dev=True, workspace_only=True):
    for window, pid in workspace_windows("MegaProxy DEV" if dev else "MegaProxy"):
        process = kernel32.OpenProcess(0x1000 | 0x100000, False, pid)
        if not process:
            raise ct.WinError(ct.get_last_error())
        try:
            image, size = ct.create_unicode_buffer(32768), wt.DWORD(32768)
            if not kernel32.QueryFullProcessImageNameW(process, 0, image, ct.byref(size)):
                raise ct.WinError(ct.get_last_error())
            path = Path(image.value).resolve()
            if path.name.casefold() != (app_name(dev) + ".exe").casefold() or workspace_only and not path.is_relative_to(ROOT):
                continue
            print(f"Stopping MegaProxy PID {pid} gracefully...", flush=True)
            reply = ct.c_size_t()
            user32.SendMessageTimeoutW(window, 0x8487, 0, 0, 2, 1000, ct.byref(reply))
            if reply.value != 0x487:
                raise RuntimeError("This older build needs a one-time Exit from its tray menu before updating.")
            if kernel32.WaitForSingleObject(process, 30000) != 0:
                raise RuntimeError("MegaProxy did not exit within 30 seconds. Finish the current operation and retry.")
        finally:
            kernel32.CloseHandle(process)


def configured_mode():
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy.Dev") as key:
            mode = winreg.QueryValueEx(key, "Mode")[0]
    except FileNotFoundError:
        return "service" if service_state(app_name(True)) is not None else "manual"
    if mode not in ("manual", "login", "service"):
        raise RuntimeError("Invalid DEV operating mode. Use cleanup-dev.cmd to reset it.")
    return mode


def restart():
    require_production_stopped()
    mode = configured_mode()
    if mode != "manual":
        sp.run([sys.executable, str(ROOT / "scripts/install.py"), "--dev", "--launch", "--mode", mode, "--elevated"], check=True)
        return
    stop_workspace_app(workspace_only=False)
    stop_service(app_name(True))
    build.main(dev=True, app_only=True)
    require_production_stopped()
    env = os.environ.copy()
    env["DOTNET_ROOT"] = str(ROOT / ".tools/dotnet")
    for directory, variable in (("sing-box", "WINDOWSMEGAPROXY_CORE"), ("sing-box-h3", "WINDOWSMEGAPROXY_H3_CORE")):
        destination = ROOT / ".tools/dev-cores" / directory
        destination.mkdir(parents=True, exist_ok=True)
        for source in (ROOT / ".tools" / directory).iterdir():
            if source.is_file():
                target = destination / ("sing-box-dev.exe" if source.name == "sing-box.exe" else source.name)
                if not target.exists() or (target.stat().st_size, target.stat().st_mtime_ns) != (source.stat().st_size, source.stat().st_mtime_ns):
                    shutil.copy2(source, target)
        env[variable] = str(destination / "sing-box-dev.exe")
    sp.Popen([str(artifact_root(True) / "app/WindowsMegaProxy.Dev.exe")], cwd=ROOT, env=env)


def main():
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate(Path(__file__))
        return
    restart()


if __name__ == "__main__":
    elevated_cli(main)
