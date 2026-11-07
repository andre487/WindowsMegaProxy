"""Install the shared service and UI; elevation is needed only for installation."""
import ctypes as ct
import csv
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
import shutil
import subprocess as sp
import sys
import tempfile
from ctypes import wintypes as wt

from common import ROOT, cli, app_name, artifact_root
from dependencies import install_core, runtime_installer
from service_control import stop_service, service_state, require_production_stopped


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


def install():
    dev = "--dev" in sys.argv
    if dev:
        require_production_stopped()
    name = app_name(dev)
    sources = (artifact_root(dev) / "app", artifact_root(dev) / "service")
    if not (sources[0] / (name + ".exe")).is_file() or not (sources[1] / (name + ".Service.exe")).is_file():
        raise RuntimeError("Run build.cmd" + (" --dev" if dev else "") + " before installation.")
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate()
        return
    target = known_folder(0x26) / name
    data = known_folder(0x23) / name
    for directory in (target, data):
        if directory.resolve() != directory.absolute():
            raise RuntimeError("The installation path contains a junction or symbolic link.")
        secure_directory(directory, public=directory == target)
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    runtime_root = known_folder(0x26) / "dotnet/shared/Microsoft.WindowsDesktop.App"
    if not dev or not runtime_root.exists() or not any(path.is_dir() for path in runtime_root.glob("10.*")):
        installer = runtime_installer(cache)
        result = sp.run([str(installer), "/install", "/passive", "/norestart"], check=False)
        if result.returncode not in (0, 3010):
            raise RuntimeError(f".NET installer exited with {result.returncode}.")
    stop_service(name)
    for source in sources:
        for entry in source.rglob("*"):
            if entry.is_symlink() or entry.is_junction():
                raise RuntimeError("Application files cannot contain links.")
            destination = target / entry.relative_to(source)
            if not destination.resolve().is_relative_to(target.resolve()):
                raise RuntimeError("Installation file escapes the application directory.")
            if entry.is_dir():
                destination.mkdir(exist_ok=True)
            else:
                shutil.copy2(entry, destination)
    install_core(target / "dependencies/sing-box", cache)
    install_core(target / "dependencies/sing-box-h3", cache, http3=True)
    if dev:
        for directory in ("sing-box", "sing-box-h3"):
            (target / "dependencies" / directory / "sing-box.exe").replace(target / "dependencies" / directory / "sing-box-dev.exe")
        require_production_stopped()
    binary = f'"{target / (name + ".Service.exe")}"'
    startup = "demand" if dev else "auto"
    if service_state(name) is None:
        run("sc.exe", "create", name, "binPath=", binary, "start=", startup, "obj=", "LocalSystem")
    else:
        run("sc.exe", "config", name, "binPath=", binary, "start=", startup, "obj=", "LocalSystem")
    run("sc.exe", "failure", name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000")
    run("sc.exe", "start", name)
    if not dev:
        user = next(csv.reader([run("whoami.exe", "/user", "/fo", "csv", "/nh").stdout.strip()]))
        if len(user) == 2 and user[1].startswith("S-1-"):
            run("schtasks.exe", "/Delete", "/TN", "WindowsMegaProxy-" + user[1], "/F", check=False)
    print(f"Installed: {target / (name + '.exe')}")


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
