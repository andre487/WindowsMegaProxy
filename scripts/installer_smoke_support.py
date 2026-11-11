"""Shared cleanup for disposable installer fixtures, including pre-Burn releases."""

import os
import re
import subprocess as sp
import winreg
from pathlib import Path

from install import known_folder


def uninstall(log, *, personal=False):
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise RuntimeError("Uninstall fixtures require a disposable GitHub-hosted runner.")
    root = known_folder(0x1c) / "Programs/WindowsMegaProxy" if personal else known_folder(0x26) / "WindowsMegaProxy"
    legacy = root / "unins000.exe"
    if legacy.is_file():
        sp.run([str(legacy), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={log}"], check=True, timeout=600)
        return
    hive = winreg.HKEY_CURRENT_USER if personal else winreg.HKEY_LOCAL_MACHINE
    try:
        uninstall_key = winreg.OpenKey(hive, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY)
    except FileNotFoundError:
        return
    candidates = []
    with uninstall_key:
        for index in range(winreg.QueryInfoKey(uninstall_key)[0]):
            with winreg.OpenKey(uninstall_key, winreg.EnumKey(uninstall_key, index)) as key:
                try:
                    if winreg.QueryValueEx(key, "DisplayName")[0] != "MegaProxy" or winreg.QueryValueEx(key, "Publisher")[0] != "MegaProxy":
                        continue
                    candidates.append(winreg.QueryValueEx(key, "QuietUninstallString")[0])
                except FileNotFoundError:
                    continue
    if len(candidates) > 1:
        raise RuntimeError("Multiple MegaProxy bundles are registered in the fixture scope.")
    if not candidates:
        return
    command = candidates[0]
    match = re.match(r'^"([^"]+)"\s', command)
    if not match:
        raise RuntimeError("Invalid registered bundle command.")
    executable = Path(match[1]).resolve()
    caches = [known_folder(0x23) / "Package Cache", known_folder(0x1c) / "Package Cache"]
    if not any(executable.is_relative_to(cache.resolve()) for cache in caches):
        raise RuntimeError("Registered bundle is outside the Windows installer cache.")
    # CreateProcess receives the registry command directly; no command shell interprets it.
    sp.run(command + " " + sp.list2cmdline(["-log", str(log), "-norestart"]), check=True, timeout=600)
