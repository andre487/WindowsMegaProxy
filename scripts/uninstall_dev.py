"""Remove only the installed DEV service/files; preserve user and machine data."""
import ctypes as ct
from pathlib import Path
import shutil
import subprocess as sp
import time

from common import ROOT, dotnet
from install import elevate, elevated_cli, known_folder, secure_directory
from run import stop_workspace_app
from service_control import service_state, stop_service, system_executable


def main():
    if not ct.windll.shell32.IsUserAnAdmin():
        elevate(Path(__file__))
        return
    name = "WindowsMegaProxy.Dev"
    target = known_folder(0x26) / name
    if target.resolve() != target.absolute() or target.name != name:
        raise RuntimeError("Unsafe DEV installation path.")
    if target.exists():
        secure_directory(target, public=True)
        if any(path.is_symlink() or path.is_junction() for path in target.rglob("*")):
            raise RuntimeError("DEV installation contains links; refusing recursive removal.")
    stop_workspace_app(dev=True, workspace_only=False)
    if service_state(name) not in (None, 1):
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
    if target.exists():
        shutil.rmtree(target)
    print("Removed DEV service and installed files. Configuration was preserved; production was not changed.")


if __name__ == "__main__":
    elevated_cli(main)
