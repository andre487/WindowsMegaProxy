"""Download prerequisites from official sources; never install the development SDK."""
import argparse
import subprocess as sp
import tempfile
from pathlib import Path

from common import cli
from dependencies import install_core, runtime_installer


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path, nargs="?", help="Installed application directory")
    parser.add_argument("--runtime-only", action="store_true")
    parser.add_argument("--download-only", action="store_true", help="Verify downloads without installing .NET")
    args = parser.parse_args()
    if not args.runtime_only and args.directory is None:
        parser.error("provide the installed application directory or --runtime-only")
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    if not args.runtime_only:
        install_core(args.directory.resolve() / "dependencies/sing-box", cache)
        install_core(args.directory.resolve() / "dependencies/sing-box-h3", cache, http3=True)
    installer = runtime_installer(cache)
    if args.download_only:
        print(f"Verified runtime installer: {installer}")
        return
    # Microsoft's installer detects existing runtimes and handles its own UAC prompt.
    result = sp.run([str(installer), "/install", "/passive", "/norestart"], check=False)
    if result.returncode not in (0, 3010):
        raise RuntimeError(f".NET Desktop Runtime installer exited with {result.returncode}.")
    print("Dependencies installed." + (" Windows restart required." if result.returncode == 3010 else ""))


if __name__ == "__main__":
    cli(main)
