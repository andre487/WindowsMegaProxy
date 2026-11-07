"""Developer installation using the same protected mode helper as native setup."""
import argparse
import ctypes as ct
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
import subprocess as sp
import sys
import tempfile
import time
import re
from ctypes import wintypes as wt

from common import ROOT, cli, app_name, artifact_root
from dependencies import install_core, runtime_installer, release
from package import merge


def run(*args, check=True):
    result = sp.run(args, check=False, capture_output=True, text=True)
    if check and result.returncode:
        raise RuntimeError(f"{args[0]} failed ({result.returncode}): {result.stderr or result.stdout}")
    return result


def known_folder(identifier):
    shell = ct.WinDLL("shell32", use_last_error=True)
    shell.SHGetFolderPathW.argtypes = [wt.HWND, ct.c_int, wt.HANDLE, wt.DWORD, wt.LPWSTR]
    path = ct.create_unicode_buffer(260)
    if shell.SHGetFolderPathW(None, identifier, None, 0, path) != 0:
        raise RuntimeError("Could not resolve the Windows installation directory.")
    return Path(path.value)


def elevate(script=None):
    class ShellExecuteInfo(ct.Structure):
        _fields_ = [("cbSize", wt.DWORD), ("fMask", wt.ULONG), ("hwnd", wt.HWND),
                    ("lpVerb", wt.LPCWSTR), ("lpFile", wt.LPCWSTR), ("lpParameters", wt.LPCWSTR),
                    ("lpDirectory", wt.LPCWSTR), ("nShow", ct.c_int), ("hInstApp", wt.HINSTANCE),
                    ("lpIDList", ct.c_void_p), ("lpClass", wt.LPCWSTR), ("hkeyClass", wt.HKEY),
                    ("dwHotKey", wt.DWORD), ("hIcon", wt.HANDLE), ("hProcess", wt.HANDLE)]
    script = Path(script or __file__).resolve()
    log = ROOT / f".tools/{script.stem}.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    info = ShellExecuteInfo(cbSize=ct.sizeof(ShellExecuteInfo), fMask=0x40, lpVerb="runas", lpFile=sys.executable,
        lpParameters=sp.list2cmdline([str(script), *[arg for arg in sys.argv[1:] if arg != "--elevated"], "--elevated"]), lpDirectory=str(ROOT), nShow=0)
    shell = ct.WinDLL("shell32", use_last_error=True)
    shell.ShellExecuteExW.argtypes = [ct.POINTER(ShellExecuteInfo)]
    if not shell.ShellExecuteExW(ct.byref(info)):
        raise ct.WinError(ct.get_last_error())
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
    kernel.GetExitCodeProcess.argtypes = [wt.HANDLE, ct.POINTER(wt.DWORD)]
    kernel.CloseHandle.argtypes = [wt.HANDLE]
    try:
        kernel.WaitForSingleObject(info.hProcess, 0xFFFFFFFF)
        code = wt.DWORD()
        if not kernel.GetExitCodeProcess(info.hProcess, ct.byref(code)):
            raise ct.WinError(ct.get_last_error())
        if log.exists():
            print(log.read_text(encoding="utf-8", errors="replace"), end="")
        if code.value:
            raise RuntimeError(f"Installation failed. See {log}.")
    finally:
        kernel.CloseHandle(info.hProcess)


def secure_directory(path, public=False):
    existed = path.exists()
    path.mkdir(parents=True, exist_ok=True)
    if path.is_symlink() or path.is_junction():
        raise RuntimeError("Installation directories cannot be links or junctions.")
    advapi = ct.WinDLL("advapi32", use_last_error=True)
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.LocalFree.argtypes = [ct.c_void_p]
    advapi.GetNamedSecurityInfoW.argtypes = [wt.LPCWSTR, ct.c_int, wt.DWORD, ct.POINTER(ct.c_void_p), ct.c_void_p, ct.c_void_p, ct.c_void_p, ct.POINTER(ct.c_void_p)]
    advapi.GetNamedSecurityInfoW.restype = wt.DWORD
    advapi.ConvertSidToStringSidW.argtypes = [ct.c_void_p, ct.POINTER(wt.LPWSTR)]
    advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [wt.LPCWSTR, wt.DWORD, ct.POINTER(ct.c_void_p), ct.c_void_p]
    advapi.SetFileSecurityW.argtypes = [wt.LPCWSTR, wt.DWORD, ct.c_void_p]
    owner, descriptor = ct.c_void_p(), ct.c_void_p()
    if existed:
        error = advapi.GetNamedSecurityInfoW(str(path), 1, 1, ct.byref(owner), None, None, None, ct.byref(descriptor))
        if error:
            raise ct.WinError(error)
        text = wt.LPWSTR()
        try:
            if not advapi.ConvertSidToStringSidW(owner, ct.byref(text)):
                raise ct.WinError(ct.get_last_error())
            if text.value not in ("S-1-5-18", "S-1-5-32-544"):
                raise RuntimeError("The installation directory has an untrusted owner.")
        finally:
            kernel.LocalFree(text)
            kernel.LocalFree(descriptor)
    sddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + ("(A;OICI;FRFX;;;BU)" if public else "")
    descriptor = ct.c_void_p()
    if not advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ct.byref(descriptor), None):
        raise ct.WinError(ct.get_last_error())
    try:
        if not advapi.SetFileSecurityW(str(path), 0x80000005, descriptor):
            raise ct.WinError(ct.get_last_error())
    finally:
        kernel.LocalFree(descriptor)


def install_main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("manual", "login", "service"), default="manual")
    parser.add_argument("--elevated", action="store_true")
    parser.add_argument("--dev", action="store_true", help="Configure the isolated DEV installation explicitly.")
    parser.add_argument("--launch", action="store_true")
    options = parser.parse_args()
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate()
        return
    if options.dev:
        from service_control import require_production_stopped
        from run import stop_workspace_app
        import build
        require_production_stopped()
        stop_workspace_app(dev=True, workspace_only=False)
        build.main(dev=True)
    name = app_name(options.dev)
    sources = (artifact_root(options.dev) / "app", artifact_root(options.dev) / "service")
    if not (sources[0] / (name + ".exe")).is_file() or not (sources[1] / (name + ".Service.exe")).is_file():
        raise RuntimeError("Run build.cmd before installation.")
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    runtime_root = known_folder(0x26) / "dotnet/shared/Microsoft.WindowsDesktop.App"
    minimum = tuple(map(int, release()["windowsdesktop"]["version"].split(".")))
    installed = any(path.is_dir() and re.fullmatch(r"10\.\d+\.\d+", path.name) and tuple(map(int, path.name.split("."))) >= minimum
                    for path in runtime_root.glob("10.*"))
    if not installed:
        installer = runtime_installer(cache)
        result = sp.run([str(installer), "/install", "/passive", "/norestart"], check=False)
        if result.returncode not in (0, 3010):
            raise RuntimeError(f".NET installer exited with {result.returncode}.")
        if result.returncode == 3010:
            raise RuntimeError("Restart Windows before continuing installation.")
    root = known_folder(0x26) / name
    target = root / "versions" / f"local-{time.time_ns()}"
    for directory in (root, root / "versions", target):
        if directory.resolve() != directory.absolute():
            raise RuntimeError("The installation path contains a junction or symbolic link.")
        secure_directory(directory, public=True)
    for source in sources:
        if any(entry.is_symlink() or entry.is_junction() for entry in source.rglob("*")):
            raise RuntimeError("Application files cannot contain links.")
        merge(source, target)
    install_core(target / "dependencies/sing-box", cache)
    install_core(target / "dependencies/sing-box-h3", cache, http3=True)
    if options.dev:
        for directory in ("sing-box", "sing-box-h3"):
            (target / "dependencies" / directory / "sing-box.exe").rename(target / "dependencies" / directory / "sing-box-dev.exe")
    run(str(target / (name + ".Service.exe")), "--configure-mode", options.mode)
    if options.launch:
        started = sp.Popen([str(target / (name + ".exe"))], cwd=target)
        try:
            code = started.wait(timeout=2)
            if code:
                raise RuntimeError(f"The application exited during restart ({code}).")
        except sp.TimeoutExpired:
            pass
    print(f"Installed: {target / (name + '.exe')}")


def install():
    try:
        install_main()
    except (OSError, RuntimeError, sp.SubprocessError, ValueError):
        if "--launch" in sys.argv and "--dev" in sys.argv:
            from run import workspace_windows
            if not workspace_windows():
                import os
                environment = os.environ.copy()
                environment["DOTNET_ROOT"] = str(ROOT / ".tools/dotnet")
                original = artifact_root(True) / "app" / (app_name(True) + ".exe")
                if original.is_file():
                    sp.Popen([str(original)], cwd=ROOT, env=environment)
            ct.windll.user32.MessageBoxW(None, "Could not finish changing the operating mode. The interface has been reopened when possible. See .tools/install.log.", "MegaProxy DEV", 0x10)
        raise


def elevated_cli(main):
    if "--elevated" in sys.argv:
        log = ROOT / f".tools/{Path(sys.argv[0]).stem}.log"
        log.parent.mkdir(parents=True, exist_ok=True)
        with log.open("w", encoding="utf-8", buffering=1) as output, redirect_stdout(output), redirect_stderr(output):
            cli(main)
    else:
        cli(main)


if __name__ == "__main__":
    elevated_cli(install)
