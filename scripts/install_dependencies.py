"""Install production prerequisites with WinGet; keep diagnostic downloads separate."""
import argparse
import tempfile
from pathlib import Path

from common import cli
from dependencies import install_core, install_machine_dependencies, runtime_installer


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path, nargs="?", help="Legacy argument; installation is now machine-wide through WinGet")
    parser.add_argument("--runtime-only", action="store_true")
    parser.add_argument("--download-only", action="store_true", help="Verify downloads without installing .NET")
    args = parser.parse_args()
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    if args.download_only:
        if not args.runtime_only:
            install_core(cache / "verified/sing-box", cache)
        installer = runtime_installer(cache)
        print(f"Verified runtime installer: {installer}")
        return
    install_machine_dependencies(runtime_only=args.runtime_only)
    print("Dependencies installed through WinGet. HTTP/3 is installed on demand from the application.")


if __name__ == "__main__":
    cli(main)
