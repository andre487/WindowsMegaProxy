"""Install the shared service and UI; elevation is needed only for installation."""
import ctypes as ct
import csv
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
import shutil
import subprocess as sp
import sys
import tempfile
import time
from ctypes import wintypes as wt

from common import ROOT, cli
from dependencies import install_core, runtime_installer


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


def elevate():
    class ShellExecuteInfo(ct.Structure):
        _fields_ = [("cbSize", wt.DWORD), ("fMask", wt.ULONG), ("hwnd", wt.HWND),
                    ("lpVerb", wt.LPCWSTR), ("lpFile", wt.LPCWSTR), ("lpParameters", wt.LPCWSTR),
                    ("lpDirectory", wt.LPCWSTR), ("nShow", ct.c_int), ("hInstApp", wt.HINSTANCE),
                    ("lpIDList", ct.c_void_p), ("lpClass", wt.LPCWSTR), ("hkeyClass", wt.HKEY),
                    ("dwHotKey", wt.DWORD), ("hIcon", wt.HANDLE), ("hProcess", wt.HANDLE)]
    log = ROOT / ".tools/install.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    info = ShellExecuteInfo(cbSize=ct.sizeof(ShellExecuteInfo), fMask=0x40, lpVerb="runas", lpFile=sys.executable,
        lpParameters=sp.list2cmdline([str(Path(__file__).resolve()), "--elevated"]), lpDirectory=str(ROOT), nShow=0)
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
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate()
        return
    target = known_folder(0x26) / "WindowsMegaProxy"
    data = known_folder(0x23) / "WindowsMegaProxy"
    for directory in (target, data):
        if directory.resolve() != directory.absolute():
            raise RuntimeError("The installation path contains a junction or symbolic link.")
        secure_directory(directory, public=directory == target)
    for source in (ROOT / "artifacts/app", ROOT / "artifacts/service"):
        if not source.is_dir():
            raise RuntimeError("Run build.cmd before install.cmd.")
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    installer = runtime_installer(cache)
    result = sp.run([str(installer), "/install", "/passive", "/norestart"], check=False)
    if result.returncode not in (0, 3010):
        raise RuntimeError(f".NET installer exited with {result.returncode}.")
    run("sc.exe", "stop", "WindowsMegaProxy", check=False)
    for _ in range(60):
        state = run("sc.exe", "query", "WindowsMegaProxy", check=False)
        if state.returncode == 1060 or "STOPPED" in state.stdout or " 1 " in state.stdout:
            break
        time.sleep(0.5)
    else:
        raise RuntimeError("The service did not stop. Installation was cancelled.")
    for source in (ROOT / "artifacts/app", ROOT / "artifacts/service"):
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
    binary = f'"{target / "WindowsMegaProxy.Service.exe"}"'
    if run("sc.exe", "query", "WindowsMegaProxy", check=False).returncode == 1060:
        run("sc.exe", "create", "WindowsMegaProxy", "binPath=", binary, "start=", "auto", "obj=", "LocalSystem")
    else:
        run("sc.exe", "config", "WindowsMegaProxy", "binPath=", binary, "start=", "auto", "obj=", "LocalSystem")
    run("sc.exe", "failure", "WindowsMegaProxy", "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000")
    run("sc.exe", "start", "WindowsMegaProxy")
    user = next(csv.reader([run("whoami.exe", "/user", "/fo", "csv", "/nh").stdout.strip()]))
    if len(user) == 2 and user[1].startswith("S-1-"):
        run("schtasks.exe", "/Delete", "/TN", "WindowsMegaProxy-" + user[1], "/F", check=False)
    print(f"Installed: {target / 'WindowsMegaProxy.exe'}")


if __name__ == "__main__":
    if "--elevated" in sys.argv:
        with (ROOT / ".tools/install.log").open("w", encoding="utf-8", buffering=1) as output, redirect_stdout(output), redirect_stderr(output):
            cli(install)
    else:
        cli(install)
