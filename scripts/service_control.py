"""Query service state without parsing localized sc.exe output."""
import csv
import ctypes as ct
import subprocess as sp
import time
from ctypes import wintypes as wt
from pathlib import Path


def system_executable(name):
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.GetSystemDirectoryW.argtypes = [wt.LPWSTR, wt.UINT]
    path = ct.create_unicode_buffer(32768)
    if not kernel.GetSystemDirectoryW(path, len(path)):
        raise ct.WinError(ct.get_last_error())
    return str(Path(path.value) / name)


def service_state(name):
    api = ct.WinDLL("advapi32", use_last_error=True)
    api.OpenSCManagerW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, wt.DWORD]
    api.OpenSCManagerW.restype = wt.HANDLE
    api.OpenServiceW.argtypes = [wt.HANDLE, wt.LPCWSTR, wt.DWORD]
    api.OpenServiceW.restype = wt.HANDLE
    api.QueryServiceStatus.argtypes = [wt.HANDLE, ct.c_void_p]
    api.CloseServiceHandle.argtypes = [wt.HANDLE]
    manager = api.OpenSCManagerW(None, None, 1)
    if not manager:
        raise ct.WinError(ct.get_last_error())
    service = None
    try:
        service = api.OpenServiceW(manager, name, 4)
        if not service:
            if ct.get_last_error() == 1060:
                return None
            raise ct.WinError(ct.get_last_error())
        status = (wt.DWORD * 7)()
        if not api.QueryServiceStatus(service, status):
            raise ct.WinError(ct.get_last_error())
        return status[1]
    finally:
        if service:
            api.CloseServiceHandle(service)
        api.CloseServiceHandle(manager)


def running_images():
    result = sp.run([system_executable("tasklist.exe"), "/FO", "CSV", "/NH"], check=True, capture_output=True, text=True)
    return {row[0].casefold() for row in csv.reader(result.stdout.splitlines()) if row}


def require_production_stopped():
    state = service_state("WindowsMegaProxy")
    if state not in (None, 1):
        raise RuntimeError("Production MegaProxy service is running. Run stop-prod.cmd before starting DEV.")
    if running_images() & {"windowsmegaproxy.exe", "windowsmegaproxy.service.exe"}:
        raise RuntimeError("Production MegaProxy is running. Run stop-prod.cmd before starting DEV.")


def stop_service(name):
    for _ in range(60):
        state = service_state(name)
        if state in (None, 1):
            return
        if state != 3:
            result = sp.run([system_executable("sc.exe"), "stop", name], check=False, capture_output=True, text=True)
            if result.returncode not in (0, 1061, 1062):
                raise RuntimeError(f"Could not stop {name}: {result.stderr or result.stdout}")
        time.sleep(0.5)
    raise RuntimeError(f"Service {name} did not stop within 30 seconds.")
