"""Local SSH forwarding fixture; no Windows service, TUN or external traffic."""
import io
import json
import logging
import select
import socket
import subprocess as sp
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import paramiko

from common import ROOT, cli, dotnet


class Origin(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Length', '11')
        self.end_headers()
        self.wfile.write(b'ssh-fixture')

    def log_message(self, *_):
        pass


def main():
    logging.getLogger('paramiko').setLevel(logging.CRITICAL)
    directory = ROOT / 'artifacts/ssh-checks'
    directory.mkdir(parents=True, exist_ok=True)
    host_key, client_key, other_key = (paramiko.RSAKey.generate(2048) for _ in range(3))
    ecdsa_key = paramiko.ECDSAKey.generate()
    origin = ThreadingHTTPServer(('127.0.0.1', 0), Origin)
    threading.Thread(target=origin.serve_forever, daemon=True).start()
    stop = threading.Event()
    sessions = []

    class Server(paramiko.ServerInterface):
        def __init__(self):
            self.destinations = {}

        def check_auth_password(self, username, password):
            return paramiko.AUTH_SUCCESSFUL if username == 'test' and password == 'secret' else paramiko.AUTH_FAILED

        def check_auth_publickey(self, username, key):
            return paramiko.AUTH_SUCCESSFUL if username == 'test' and key == client_key else paramiko.AUTH_FAILED

        def get_allowed_auths(self, _):
            return 'publickey,password'

        def check_channel_direct_tcpip_request(self, _channel, _source, destination):
            if destination not in (('127.0.0.1', origin.server_port), listener.getsockname()):
                return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
            self.destinations[_channel] = destination
            return paramiko.OPEN_SUCCEEDED

    def relay(channel, destination):
        try:
            with socket.create_connection(destination, timeout=5) as target:
                target.settimeout(None)
                while not stop.is_set():
                    for source in select.select([channel, target], [], [], 0.5)[0]:
                        data = source.recv(65536)
                        if not data:
                            return
                        (target if source is channel else channel).sendall(data)
        finally:
            channel.close()

    def serve_peer(peer):
        transport = paramiko.Transport(peer)
        sessions.append(transport)
        try:
            transport.add_server_key(host_key)
            transport.add_server_key(ecdsa_key)
            server = Server()
            transport.start_server(server=server)
            while transport.is_active() and not stop.is_set():
                channel = transport.accept(0.5)
                if channel is not None:
                    threading.Thread(target=relay, args=(channel, server.destinations[channel.chanid]), daemon=True).start()
        except (paramiko.SSHException, EOFError, OSError):
            pass
        finally:
            transport.close()

    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        listener.settimeout(0.5)

        def accept():
            while not stop.is_set():
                try:
                    peer, _ = listener.accept()
                except socket.timeout:
                    continue
                except OSError:
                    return
                threading.Thread(target=serve_peer, args=(peer,), daemon=True).start()

        threading.Thread(target=accept, daemon=True).start()
        private_key = io.StringIO()
        client_key.write_private_key(private_key)
        fixture = directory / 'fixture.json'
        fixture.write_text(json.dumps(dict(port=listener.getsockname()[1], origin=f'http://127.0.0.1:{origin.server_port}/',
            hostKey=host_key.get_name() + ' ' + host_key.get_base64(), otherKey=other_key.get_name() + ' ' + other_key.get_base64(),
            privateKey=private_key.getvalue())), encoding='utf-8')
        try:
            try:
                dotnet('run', '--project', ROOT / 'tests/WindowsMegaProxy.UiChecks', '-c', 'Release', '--', '--ssh-check', fixture)
            except sp.CalledProcessError:
                report = directory / 'failure.txt'
                if report.exists():
                    print(report.read_text(encoding='utf-8'))
                raise
            print((directory / 'result.txt').read_text(encoding='utf-8'))
        finally:
            fixture.unlink(missing_ok=True)
            stop.set()
            for session in sessions:
                session.close()
            origin.shutdown()
            origin.server_close()


if __name__ == '__main__':
    cli(main)
