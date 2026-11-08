"""Sign packaged MegaProxy binaries without changing Windows certificate trust."""

import base64
import csv
import os
import subprocess as sp
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

from common import ROOT, cli, dotnet

SDK_VERSION = "10.0.28000.2705"
PUBLIC_CERTIFICATE = Path(__file__).resolve().parent.parent / "assets/authenticode-certificate.pem"
SECRET_DIRECTORY = Path(os.environ.get("LOCALAPPDATA", "")) / "MegaProxy/DeveloperSecrets/WindowsMegaProxy/code-signing"


def preview_certificate():
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "MegaProxy disposable preview")])
    now = datetime.now(timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - timedelta(minutes=5))
            .not_valid_after(now + timedelta(days=2))
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.CODE_SIGNING]), critical=False)
            .sign(key, hashes.SHA256()))
    return key, cert


def material(preview):
    encoded = os.environ.pop("WINDOWS_SIGNING_PFX", "")
    password = os.environ.pop("WINDOWS_SIGNING_PASSWORD", "")
    if bool(encoded) != bool(password):
        raise RuntimeError("Both Windows signing secrets are required.")
    if encoded:
        try:
            data = base64.b64decode(encoded, validate=True)
        except ValueError:
            raise RuntimeError("Invalid Windows signing certificate encoding.") from None
    elif (SECRET_DIRECTORY / "code-signing.pfx").is_file():
        data = (SECRET_DIRECTORY / "code-signing.pfx").read_bytes()
        password = (SECRET_DIRECTORY / "code-signing-password.txt").read_text(encoding="utf-8-sig").strip()
    elif preview:
        return preview_certificate()
    else:
        raise RuntimeError("The permanent Windows signing certificate is required for releases.")
    try:
        key, cert, _ = pkcs12.load_key_and_certificates(data, password.encode("utf-8"))
    except ValueError:
        raise RuntimeError("Could not unlock the Windows signing certificate.") from None
    pinned = x509.load_pem_x509_certificate(PUBLIC_CERTIFICATE.read_bytes())
    if key is None or cert is None or cert.fingerprint(hashes.SHA256()) != pinned.fingerprint(hashes.SHA256()):
        raise RuntimeError("Windows signing certificate does not match the repository's public certificate.")
    try:
        usage = cert.extensions.get_extension_for_class(x509.ExtendedKeyUsage).value
    except x509.ExtensionNotFound:
        raise RuntimeError("Windows certificate has no code-signing usage.") from None
    if key.public_key() != cert.public_key() or ExtendedKeyUsageOID.CODE_SIGNING not in usage:
        raise RuntimeError("Invalid Windows code-signing key or certificate usage.")
    now = datetime.now(timezone.utc)
    if not cert.not_valid_before_utc <= now < cert.not_valid_after_utc:
        raise RuntimeError("Windows signing certificate is not currently valid.")
    return key, cert


def signing_tool():
    directory = ROOT / ".tools/nuget/microsoft.windows.sdk.buildtools" / SDK_VERSION
    tool = directory / "bin/10.0.28000.0/x64/signtool.exe"
    if not tool.is_file():
        project = ROOT / ".tools/signing-sdk/SigningSdk.csproj"
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            '    <PropertyGroup>\n'
            '        <TargetFramework>net10.0-windows</TargetFramework>\n'
            '    </PropertyGroup>\n'
            '    <ItemGroup>\n'
            f'        <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="{SDK_VERSION}" />\n'
            '    </ItemGroup>\n'
            '</Project>\n', encoding="utf-8")
        dotnet("restore", str(project), "--source", "https://api.nuget.org/v3/index.json", "--nologo")
    if not tool.is_file():
        raise RuntimeError("Official Windows SDK SignTool is missing.")
    return tool


def sign_file(path, environment):
    path = Path(path).resolve(strict=True)
    tool = environment["MEGAPROXY_SIGNTOOL"]
    command = [tool, "sign", "/q", "/f", environment["MEGAPROXY_SIGNING_PFX"], "/fd", "SHA256"]
    if environment.get("MEGAPROXY_SIGNING_TIMESTAMP") == "1":
        # SignTool requires HTTP for this TSA; the RFC 3161 response is itself signed.
        command.extend(["/tr", "http://timestamp.digicert.com", "/td", "SHA256"])
    result = sp.run([*command, str(path)], env=environment, capture_output=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"Authenticode signing failed for {path.name}.")
    result = sp.run([tool, "verify", "/pa", "/v", str(path)], env=environment, capture_output=True,
                    text=True, encoding="utf-8", errors="replace", timeout=180)
    output = result.stdout + result.stderr
    untrusted_root_only = (result.returncode == 1 and "certificate which is not trusted" in output
                           and "Number of errors: 1" in output)
    if result.returncode != 0 and not untrusted_root_only:
        raise RuntimeError(f"Authenticode verification failed for {path.name}.")


class Signer:
    def __init__(self, *, preview):
        self.key, self.certificate = material(preview)
        self.fingerprint = self.certificate.fingerprint(hashes.SHA256()).hex()
        self.preview = preview
        self.temporary = None

    def __enter__(self):
        tool = signing_tool()
        self.temporary = tempfile.TemporaryDirectory(prefix="authenticode-", dir=ROOT / ".tools")
        try:
            directory = Path(self.temporary.name)
            system = Path(os.environ["SystemRoot"]) / "System32"
            identity = sp.check_output([str(system / "whoami.exe"), "/user", "/fo", "csv", "/nh"], text=True)
            sid = next(csv.reader(identity.splitlines()))[1]
            # SignTool gets no password argument; its temporary PFX is private to this user and SYSTEM.
            sp.run([str(system / "icacls.exe"), str(directory), "/inheritance:r", "/grant:r",
                    f"*{sid}:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F"], check=True, capture_output=True)
            pfx = directory / "signing.pfx"
            pfx.write_bytes(pkcs12.serialize_key_and_certificates(b"MegaProxy", self.key, self.certificate, None,
                                                                 serialization.NoEncryption()))
            environment = {name: value for name, value in os.environ.items()
                           if name not in ("WINDOWS_SIGNING_PFX", "WINDOWS_SIGNING_PASSWORD")}
            self.environment = environment | {"MEGAPROXY_SIGNTOOL": str(tool), "MEGAPROXY_SIGNING_PFX": str(pfx),
                                              "MEGAPROXY_SIGNING_TIMESTAMP": "0" if self.preview else "1"}
            return self
        except BaseException:
            self.temporary.cleanup()
            raise

    def __exit__(self, *args):
        self.temporary.cleanup()

    def sign_directory(self, directory):
        for path in sorted(Path(directory).rglob("*")):
            if path.is_file() and path.name.startswith("WindowsMegaProxy") and path.suffix.lower() in (".exe", ".dll"):
                sign_file(path, self.environment)

    def inno_tool(self):
        return f'megaproxy=$q{sys.executable}$q $q{Path(__file__).resolve()}$q $f'


if __name__ == "__main__":
    cli(lambda: sign_file(sys.argv[1], os.environ))
