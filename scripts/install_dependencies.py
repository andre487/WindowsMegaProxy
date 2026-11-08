"""Prepare pinned proxy cores and the mandatory Windows OpenSSH Client."""
import argparse
import tempfile
from pathlib import Path

from common import cli
from dependencies import install_core, install_openssh


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path, help="Application directory for the bundled core files")
    parser.add_argument("--download-only", action="store_true", help="Verify core downloads without installing OpenSSH")
    args = parser.parse_args()
    cache = Path(tempfile.gettempdir()) / "WindowsMegaProxy-downloads"
    install_core(args.directory.resolve() / "dependencies/sing-box", cache)
    install_core(args.directory.resolve() / "dependencies/sing-box-h3", cache, http3=True)
    if not args.download_only:
        install_openssh()
    print("Pinned proxy cores ready. .NET is included in the application.")


if __name__ == "__main__":
    cli(main)
