"""Exercise DLL lifecycle, local SOCKS traffic and mutual publisher verification."""

import argparse
import ctypes as ct
import json
import shutil
import socket
import struct
import subprocess as sp
import tempfile
import threading
import time
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.serialization import pkcs12

import build_core
from authenticode import Signer, preview_certificate, sign_file
from common import ROOT, cli


def request(config, operation=0):
    data = json.dumps(config).encode()
    return struct.pack("<II", operation, len(data)) + data


CONFIG = {"log": {"disabled": True}, "outbounds": [{"type": "direct", "tag": "direct"}], "route": {"final": "direct"}}


def run(host, variant, config=CONFIG, operation=0):
    return sp.run([str(host), variant], input=request(config, operation), capture_output=True, timeout=30)


def receive(connection, count):
    result = b""
    while len(result) < count:
        chunk = connection.recv(count - len(result))
        if not chunk:
            raise RuntimeError("Unexpected end of SOCKS connection.")
        result += chunk
    return result


def traffic(host, variant):
    with socket.socket() as echo, socket.socket() as reservation:
        echo.bind(("127.0.0.1", 0))
        echo.listen()
        echo.settimeout(15)
        reservation.bind(("127.0.0.1", 0))
        proxy_port = reservation.getsockname()[1]
        reservation.close()
        errors = []

        def server():
            try:
                with echo.accept()[0] as connection:
                    connection.settimeout(10)
                    connection.sendall(receive(connection, 11))
            except Exception as error:
                errors.append(error)

        worker = threading.Thread(target=server)
        worker.start()
        config = CONFIG | {"inbounds": [{"type": "socks", "listen": "127.0.0.1", "listen_port": proxy_port}]}
        with sp.Popen([str(host), variant], stdin=sp.PIPE, stdout=sp.PIPE, stderr=sp.PIPE) as process:
            try:
                process.stdin.write(request(config, 1))
                process.stdin.flush()
                deadline = time.monotonic() + 15
                while True:
                    try:
                        proxy = socket.create_connection(("127.0.0.1", proxy_port), timeout=1)
                        break
                    except OSError:
                        if process.poll() is not None or time.monotonic() >= deadline:
                            raise RuntimeError("Native proxy did not start.")
                        time.sleep(0.05)
                with proxy:
                    proxy.settimeout(10)
                    proxy.sendall(b"\x05\x01\x00")
                    assert receive(proxy, 2) == b"\x05\x00"
                    proxy.sendall(b"\x05\x01\x00\x01\x7f\x00\x00\x01" + struct.pack("!H", echo.getsockname()[1]))
                    reply = receive(proxy, 4)
                    assert reply[:2] == b"\x05\x00"
                    receive(proxy, (4 if reply[3] == 1 else 16) + 2)
                    proxy.sendall(b"native-core")
                    assert receive(proxy, 11) == b"native-core"
                process.stdin.close()
                process.wait(timeout=10)
                assert process.returncode == 0, process.stderr.read().decode(errors="replace")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                worker.join(timeout=16)
        assert not errors, errors


def tun(host, variant):
    if not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("TUN smoke checks require an administrator token.")
    config = CONFIG | {"inbounds": [{"type": "tun", "interface_name": "MegaProxy DLL smoke",
                                    "address": ["192.0.2.1/30"], "auto_route": False, "stack": "system"}]}
    ready = threading.Event()
    messages = []
    with sp.Popen([str(host), variant], stdin=sp.PIPE, stdout=sp.DEVNULL, stderr=sp.PIPE) as process:
        def drain():
            for line in process.stderr:
                messages.append(line.decode(errors="replace"))
                if b"MegaProxy core started." in line:
                    ready.set()

        reader = threading.Thread(target=drain)
        reader.start()
        try:
            process.stdin.write(request(config, 1))
            process.stdin.flush()
            if not ready.wait(20):
                raise RuntimeError("TUN did not start: " + "".join(messages))
            process.stdin.close()
            process.wait(timeout=10)
            assert process.returncode == 0, messages
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            reader.join(timeout=5)


def main(signed=False, check_tun=False):
    dev = build_core.main(dev=True)
    build_core.install(ROOT / ".tools/dev-cores/sing-box", dev=True)
    build_core.install(ROOT / ".tools/dev-cores/sing-box-h3", experimental=True, dev=True)
    dev_host = dev / "WindowsMegaProxy.Dev.Core.exe"
    for variant in ("stable", "experimental"):
        for invalid in (b"", struct.pack("<II", 3, 1) + b"x", struct.pack("<II", 0, 16 * 1024 * 1024 + 1),
                        struct.pack("<II", 0, 3) + b"x", struct.pack("<II", 0, 3) + b"{}\0"):
            result = sp.run([str(dev_host), variant], input=invalid, capture_output=True, timeout=30)
            assert result.returncode == 2, result.stderr
        for operation in (0, 1):
            result = run(dev_host, variant, operation=operation)
            assert result.returncode == 0, result.stderr
        assert run(dev_host, variant, {"outbounds": [{"type": "nonexistent"}]}).returncode != 0
        traffic(dev_host, variant)
        if check_tun:
            tun(dev_host, variant)
    print("DEV lifecycle, invalid configuration and local SOCKS traffic passed for both DLLs.", flush=True)
    if not signed:
        return
    with tempfile.TemporaryDirectory(prefix="core-trust-", dir=ROOT / "artifacts") as temporary, Signer(preview=True) as signer:
        directory = Path(temporary)
        publisher = directory / "publisher.pem"
        publisher.write_bytes(signer.certificate.public_bytes(serialization.Encoding.PEM))
        production = build_core.main(publisher=publisher)
        for name in ("WindowsMegaProxy.Core.exe", "MegaProxy.Core.Stable.dll", "MegaProxy.Core.Experimental.dll"):
            shutil.copy2(production / name, directory / name)
        signer.sign_directory(directory)
        host = directory / "WindowsMegaProxy.Core.exe"
        for variant in ("stable", "experimental"):
            result = run(host, variant)
            assert result.returncode == 0, result.stderr
            traffic(host, variant)
            if check_tun:
                tun(host, variant)
        signed_host = host.read_bytes()
        dll = directory / "MegaProxy.Core.Stable.dll"
        signed_dll = dll.read_bytes()
        shutil.copy2(dev_host, host)
        result = run(host, "stable")
        assert result.returncode != 0 and b"host executable" in result.stderr, result.stderr
        damaged_host = bytearray(signed_host)
        damaged_host[0x40] ^= 1
        host.write_bytes(damaged_host)
        result = run(host, "stable")
        assert result.returncode != 0 and b"host executable" in result.stderr, result.stderr
        host.write_bytes(signed_host)
        shutil.copy2(production / dll.name, dll)
        result = run(host, "stable")
        assert result.returncode != 0 and b"signature verification failed" in result.stderr, result.stderr
        damaged = bytearray(signed_dll)
        damaged[0x40] ^= 1  # DOS stub is covered by Authenticode but does not change the PE layout.
        dll.write_bytes(damaged)
        assert run(host, "stable").returncode != 0
        key, certificate = preview_certificate()
        pfx = Path(signer.temporary.name) / "other-publisher.pfx"
        pfx.write_bytes(pkcs12.serialize_key_and_certificates(b"Other", key, certificate, None, serialization.NoEncryption()))
        shutil.copy2(production / dll.name, dll)
        sign_file(dll, signer.environment | {"MEGAPROXY_SIGNING_PFX": str(pfx), "MEGAPROXY_SIGNING_TIMESTAMP": "0"})
        assert run(host, "stable").returncode != 0
        dll.write_bytes(signed_dll)
        shutil.copy2(production / host.name, host)
        sign_file(host, signer.environment | {"MEGAPROXY_SIGNING_PFX": str(pfx), "MEGAPROXY_SIGNING_TIMESTAMP": "0"})
        assert run(host, "stable").returncode != 0
    print("Mutual signature checks passed: own publisher accepted; unsigned, damaged and foreign publisher rejected.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--signed", action="store_true")
    parser.add_argument("--tun", action="store_true")
    args = parser.parse_args()
    cli(lambda: main(args.signed, args.tun))
