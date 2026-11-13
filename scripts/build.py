import argparse
import ctypes as ct
import shutil
import tempfile
from ctypes import wintypes as wt

from build_launcher import build_launcher
from common import ROOT, app_name, artifact_root, cli, dotnet


def product_version(path):
    library = ct.WinDLL("version", use_last_error=True)
    library.GetFileVersionInfoSizeW.argtypes = [wt.LPCWSTR, ct.POINTER(wt.DWORD)]
    library.GetFileVersionInfoW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ct.c_void_p]
    library.VerQueryValueW.argtypes = [ct.c_void_p, wt.LPCWSTR, ct.POINTER(ct.c_void_p), ct.POINTER(wt.UINT)]
    size = library.GetFileVersionInfoSizeW(str(path), None)
    data = ct.create_string_buffer(size)
    value, length = ct.c_void_p(), wt.UINT()
    if not size or not library.GetFileVersionInfoW(str(path), 0, size, data):
        raise RuntimeError("Published executable has no Windows version resource.")
    if not library.VerQueryValueW(data, r"\VarFileInfo\Translation", ct.byref(value), ct.byref(length)) or length.value < 4:
        raise RuntimeError("Published executable has no version translation.")
    language, codepage = ct.cast(value, ct.POINTER(wt.WORD))[:2]
    query = rf"\StringFileInfo\{language:04x}{codepage:04x}\ProductVersion"
    if not library.VerQueryValueW(data, query, ct.byref(value), ct.byref(length)):
        raise RuntimeError("Published executable has no product version.")
    return ct.wstring_at(value, length.value - 1)


def publish_options(dev=False):
    return ["--self-contained", "true", "-p:PublishSingleFile=false", "-p:DebugType=embedded"]


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
                if output == "launcher":
                    build_launcher(staging, dev=dev)
                else:
                    dotnet("publish", str(ROOT / f"src/{project}/{project}.csproj"), "-c", "Release", "-r", "win-x64",
                           *publish_options(dev=dev and output == "app"), "-o", staging, "--nologo", f"-p:MegaProxyDev={str(dev).lower()}")
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
