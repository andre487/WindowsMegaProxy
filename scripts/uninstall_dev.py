"""Remove DEV services, startup registrations and installed files; preserve data."""
import csv
import ctypes as ct
import json
import re
import shutil
import subprocess as sp
import time
import winreg
from pathlib import Path

from common import ROOT, dotnet
from install import elevate, elevated_cli, known_folder, secure_directory
from run import stop_workspace_app
from service_control import service_state, stop_service, system_executable

NAME = "WindowsMegaProxy.Dev"
RUN_KEY = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"


def is_dev_task(task):
    return re.fullmatch(r"WindowsMegaProxy\.Dev(?:\.Login|-S-1-\d+(?:-\d+)*)?", task.rsplit("\\", 1)[-1], re.IGNORECASE) is not None


def startup_records():
    result = sp.run([system_executable("schtasks.exe"), "/Query", "/FO", "CSV", "/NH"], check=True, capture_output=True, text=True)
    tasks = [row[0] for row in csv.reader(result.stdout.splitlines()) if row and is_dev_task(row[0])]
    values = []
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
            for key_path, names in ((RUN_KEY, (NAME,)), ("SOFTWARE\\" + NAME, ("Mode", "HelperPath"))):
                try:
                    with winreg.OpenKey(hive, key_path, 0, winreg.KEY_READ | view) as key:
                        for value_name in names:
                            try:
                                winreg.QueryValueEx(key, value_name)
                                values.append((hive, key_path, value_name, view))
                            except FileNotFoundError:
                                pass
                except FileNotFoundError:
                    pass
    return tasks, values


def remove_startup(tasks, values):
    for task in tasks:
        if not is_dev_task(task):
            raise RuntimeError("Refusing to remove a non-DEV task.")
        sp.run([system_executable("schtasks.exe"), "/Delete", "/TN", task, "/F"], check=True)
    for hive, key_path, value_name, view in values:
        if (key_path, value_name) not in ((RUN_KEY, NAME), ("SOFTWARE\\" + NAME, "Mode"), ("SOFTWARE\\" + NAME, "HelperPath")):
            raise RuntimeError("Refusing to remove a non-DEV registry value.")
        try:
            with winreg.OpenKey(hive, key_path, 0, winreg.KEY_SET_VALUE | view) as key:
                winreg.DeleteValue(key, value_name)
        except FileNotFoundError:
            pass
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for view in (winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
            try:
                with winreg.OpenKey(hive, "SOFTWARE\\" + NAME, 0, winreg.KEY_READ | view) as key:
                    empty = winreg.QueryInfoKey(key)[:2] == (0, 0)
                if empty:
                    winreg.DeleteKeyEx(hive, "SOFTWARE\\" + NAME, access=view)
            except FileNotFoundError:
                pass


def cleanup(*, close_clients=True, remove_files=True):
    name = NAME
    target = known_folder(0x26) / name
    if target.resolve() != target.absolute() or target.name != name:
        raise RuntimeError("Unsafe DEV installation path.")
    tasks, values = startup_records()
    state = service_state(name)
    if state is None and not tasks and not values and (not remove_files or not target.exists()):
        print("DEV has no registered service or startup entries.")
        return
    print(f"DEV audit: service={state is not None}, scheduled tasks={len(tasks)}, registry startup/mode entries={len(values)}.")
    if remove_files and target.exists():
        secure_directory(target, public=True)
        if any(path.is_symlink() or path.is_junction() for path in target.rglob("*")):
            raise RuntimeError("DEV installation contains links; refusing recursive removal.")
    if close_clients:
        stop_workspace_app(dev=True, workspace_only=False)
    data_file = known_folder(0x23) / name / "settings.json"
    legacy = data_file.exists() and json.loads(data_file.read_text(encoding="utf-8-sig")).get("scope") != "machine"
    if state is not None and legacy:
        if state == 1:
            sp.run([system_executable("sc.exe"), "start", name], check=True)
        dotnet("run", "--project", str(ROOT / "src/WindowsMegaProxy.Service"), "-c", "Release", "-p:MegaProxyDev=true", "--", "--migrate-dev-settings")
    stop_service(name)
    if service_state(name) is not None:
        sp.run([system_executable("sc.exe"), "delete", name], check=True)
        for _ in range(60):
            if service_state(name) is None:
                break
            time.sleep(0.5)
        else:
            raise RuntimeError("DEV service is pending deletion. Close Services and retry.")
    remove_startup(tasks, values)
    if remove_files and target.exists():
        shutil.rmtree(target)
    remaining_tasks, remaining_values = startup_records()
    if service_state(name) is not None or remaining_tasks or remaining_values:
        raise RuntimeError("Some DEV registrations remain; cleanup did not complete.")
    print("Removed DEV service and all DEV startup registrations. Configuration and production were preserved.")


def main():
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate(Path(__file__))
        return
    cleanup()


if __name__ == "__main__":
    elevated_cli(main)
