"""Check generated sniff routing on loopback; never create a TUN interface."""

import json
import socket
import ssl
import statistics
import struct
import subprocess as sp
import tempfile
import threading
import time
from pathlib import Path

from common import ROOT, cli, dotnet


def receive(client, count):
    data = b""
    while len(data) < count:
        chunk = client.recv(count - len(data))
        if not chunk:
            raise EOFError("Connection closed")
        data += chunk
    return data


def main():
    dotnet("run", "--project", "tests/WindowsMegaProxy.Checks", "-c", "Release", "--", "--sniff-configs")
    directory = ROOT / "artifacts/sniff-checks"
    results = []
    outgoing = ssl.MemoryBIO()
    tls = ssl.create_default_context().wrap_bio(ssl.MemoryBIO(), outgoing, server_hostname="blocked.example")
    try:
        tls.do_handshake()
    except ssl.SSLWantReadError:
        pass
    client_hello = outgoing.read()
    with socket.socket() as server:
        server.bind(("127.0.0.1", 0))
        server.listen()
        server.settimeout(0.2)
        stopping = threading.Event()

        def serve():
            while not stopping.is_set():
                try:
                    connection, _ = server.accept()
                except socket.timeout:
                    continue
                with connection:
                    connection.settimeout(2)
                    try:
                        connection.sendall(b"HELLO\n")
                        while connection.recv(4096):
                            pass
                    except OSError:
                        pass

        worker = threading.Thread(target=serve)
        worker.start()
        try:
            for core in ("sing-box", "sing-box-h3"):
                executable = ROOT / f".tools/dev-cores/{core}/WindowsMegaProxy.Dev.Core.exe"
                for name in ("plain", "scoped", "early", "global"):
                    with socket.socket() as reservation:
                        reservation.bind(("127.0.0.1", 0))
                        port = reservation.getsockname()[1]
                    config = json.loads((directory / f"{name}.json").read_text(encoding="utf-8"))
                    config["inbounds"] = [{"type": "socks", "listen": "127.0.0.1", "listen_port": port}]
                    config.pop("experimental")
                    config["log"] = {"disabled": True}
                    with tempfile.TemporaryDirectory(dir=directory) as temporary:
                        with open(Path(temporary) / "stderr.log", "w+", encoding="utf-8") as errors:
                            process = sp.Popen([str(executable), "experimental" if core == "sing-box-h3" else "stable"], stdin=sp.PIPE, stdout=sp.DEVNULL,
                                               stderr=errors, creationflags=sp.CREATE_NO_WINDOW)
                            try:
                                data = json.dumps(config).encode("utf-8")
                                process.stdin.write(struct.pack("<II", 1, len(data)) + data)
                                process.stdin.flush()
                                deadline = time.monotonic() + 10
                                while True:
                                    if process.poll() is not None:
                                        errors.seek(0)
                                        raise RuntimeError("Core exited: " + errors.read())
                                    try:
                                        with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                                            break
                                    except OSError:
                                        if time.monotonic() >= deadline:
                                            raise
                                        time.sleep(0.05)
                                for kind in ("banner", "http", "tls", "known", "known-http", "known-tls"):
                                    known = kind.startswith("known")
                                    # A SOCKS hostname has no destination IP yet, so the early IP rule cannot match it.
                                    blocked = (name == "global" or name == "early" and known) and kind.endswith(("http", "tls"))
                                    samples = []
                                    for _ in range(3):
                                        with socket.create_connection(("127.0.0.1", port), timeout=5) as client:
                                            client.sendall(b"\x05\x01\x00")
                                            assert receive(client, 2) == b"\x05\x00"
                                            address = b"\x03\x09localhost" if known else b"\x01\x7f\x00\x00\x01"
                                            start = time.perf_counter()
                                            client.sendall(b"\x05\x01\x00" + address + struct.pack("!H", server.getsockname()[1]))
                                            reply = receive(client, 4)
                                            assert reply[1] == 0
                                            receive(client, 6 if reply[3] == 1 else 18)
                                            # Unknown IP destinations must still be filtered by Host or TLS SNI.
                                            if kind.endswith("http"):
                                                client.sendall(b"GET / HTTP/1.1\r\nHost: blocked.example\r\n\r\n")
                                            elif kind.endswith("tls"):
                                                client.sendall(client_hello)
                                            try:
                                                greeting = receive(client, 6)
                                            except (EOFError, ConnectionResetError, ConnectionAbortedError):
                                                assert blocked, f"Unexpected routing rejection: {core}/{name}/{kind}"
                                            else:
                                                assert not blocked, f"Domain block was bypassed: {core}/{name}/{kind}"
                                                assert greeting == b"HELLO\n"
                                            samples.append((time.perf_counter() - start) * 1000)
                                    results.append(f"{core}/{name}/{kind}: passed, median {statistics.median(samples):.1f} ms")
                            finally:
                                process.stdin.close()
                                try:
                                    process.wait(timeout=10)
                                except sp.TimeoutExpired:
                                    process.kill()
                                    process.wait(timeout=5)
        finally:
            stopping.set()
            worker.join(timeout=5)
    (directory / "result.txt").write_text("\n".join(results), encoding="utf-8")
    print("\n".join(results))


if __name__ == "__main__":
    cli(main)
