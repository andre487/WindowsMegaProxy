import ctypes as ct
from ctypes import wintypes as wt
import shutil
import tempfile

from common import ROOT, cli, dotnet


def main():
    kernel = ct.WinDLL("kernel32", use_last_error=True)
    kernel.CreateMutexW.argtypes = [ct.c_void_p, wt.BOOL, wt.LPCWSTR]
    kernel.CreateMutexW.restype = wt.HANDLE
    kernel.CloseHandle.argtypes = [wt.HANDLE]
    mutex = kernel.CreateMutexW(None, False, "Local\\WindowsMegaProxy")
    if not mutex:
        raise RuntimeError("Cannot check whether MegaProxy is running. Close it or use run.cmd.")
    try:
        if ct.get_last_error() == 183:
            raise RuntimeError("MegaProxy is running. Use run.cmd to stop it gracefully and rebuild.")
        for project, output in (("WindowsMegaProxy", "app"), ("MegaProxy.TraceProbe", "trace-probe")):
            destination = (ROOT / f"artifacts/{output}").resolve()
            if not destination.is_relative_to((ROOT / "artifacts").resolve()):
                raise RuntimeError("Publish destination escapes artifacts.")
            destination.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix="publish-", dir=destination.parent) as staging:
                dotnet("publish", str(ROOT / f"src/{project}/{project}.csproj"), "-c", "Release", "-r", "win-x64",
                       "--self-contained", "false", "-o", staging, "--nologo")
                # Replace only after a successful publish; discard obsolete bundled runtimes.
                if destination.exists():
                    shutil.rmtree(destination)
                shutil.copytree(staging, destination)
    finally:
        kernel.CloseHandle(mutex)


if __name__ == "__main__":
    cli(main)
