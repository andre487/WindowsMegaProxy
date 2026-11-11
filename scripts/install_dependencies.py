"""Prepare pinned proxy cores and the mandatory Windows OpenSSH Client."""
import argparse
from pathlib import Path

import build_core
from authenticode import Signer
from common import cli
from dependencies import install_openssh


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path, help="Application directory for the bundled core files")
    parser.add_argument("--download-only", action="store_true", help="Prepare cores without installing OpenSSH")
    args = parser.parse_args()
    destination = args.directory.resolve() / "dependencies"
    build_core.install(destination / "sing-box")
    build_core.install(destination / "sing-box-h3", experimental=True)
    with Signer(preview=False) as signer:
        signer.sign_directory(destination)
    if not args.download_only:
        install_openssh()
    print("Pinned proxy cores ready. .NET is included in the application.")


if __name__ == "__main__":
    cli(main)
