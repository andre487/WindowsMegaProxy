"""Validate generated configurations through the DLL host, without starting TUN."""

import struct
import subprocess as sp

from common import ROOT, cli


def main():
    for folder, variant, patterns in (
        ("sing-box", "stable", ("test-config.json", "system-test-config.json", "system-exceptions-test-config.json",
                                "fingerprint-*.json", "ssh-*.json", "jump-test-config.json", "dns-checks/*.json")),
        ("sing-box-h3", "experimental", ("http3-test-config.json", "jump-http3-test-config.json", "dns-checks/*.json")),
    ):
        host = ROOT / ".tools/dev-cores" / folder / "WindowsMegaProxy.Dev.Core.exe"
        for pattern in patterns:
            files = sorted((ROOT / "artifacts").glob(pattern))
            if not files:
                raise RuntimeError(f"Missing generated test configurations: {pattern}")
            for path in files:
                data = path.read_bytes()
                sp.run([str(host), variant], input=struct.pack("<II", 0, len(data)) + data,
                       check=True, timeout=30, creationflags=sp.CREATE_NO_WINDOW)
    print("Both native DLLs accepted all generated configurations.")


if __name__ == "__main__":
    cli(main)
