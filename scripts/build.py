import argparse
import ctypes as ct
import shutil
import tempfile
from ctypes import wintypes as wt

from common import ROOT, app_name, artifact_root, cli, dotnet


def main(dev=False, app_only=False):
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ct.c_void_p, wt.BOOL, wt.LPCWSTR]
    kernel.CreateMutexW.restype = wt.HANDLE
    kernel.CloseHandle.argtypes = [wt.HANDLE]
    mutex = kernel.CreateMutexW(None, False, "Local\\" + app_name(dev))
    if not mutex:
        raise RuntimeError("Cannot check whether MegaProxy is running. Close it or use run.cmd.")
    try:
        if ct.get_last_error() == 183:
            raise RuntimeError("MegaProxy is running. Use run.cmd to stop it gracefully and rebuild.")
        for project, output in (("WindowsMegaProxy", "app"), ("WindowsMegaProxy.Service", "service"), ("MegaProxy.TraceProbe", "trace-probe"), ("WindowsMegaProxy.Launcher", "launcher")):
            if app_only and output != "app":
                continue
            destination = (artifact_root(dev) / output).resolve()
            if not destination.is_relative_to((ROOT / "artifacts").resolve()):
                raise RuntimeError("Publish destination escapes artifacts.")
            destination.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="publish-", dir=destination.parent) as staging:
                dotnet("publish", str(ROOT / f"src/{project}/{project}.csproj"), "-c", "Release", "-r", "win-x64",
                       "--self-contained", "false", "-o", staging, "--nologo", f"-p:MegaProxyDev={str(dev).lower()}")
                # Replace only after a successful publish; discard obsolete bundled runtimes.
                if destination.exists():
                    shutil.rmtree(destination)
                shutil.copytree(staging, destination)
    finally:
        kernel.CloseHandle(mutex)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--dev", action="store_true")
    cli(lambda: main(parser.parse_args().dev))
