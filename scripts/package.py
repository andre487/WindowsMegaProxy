"""Build the native installer and independently verifiable update components."""
import argparse
import base64
import json
import os
import re
import shutil
import subprocess as sp
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

import build
from authenticode import Signer
from common import ROOT, cli, digest, download, extract
from dependencies import (
    CORE_VERSION,
    H3_CORE_VERSION,
    asset,
    core_package,
    install_core,
    release,
    runtime_installer,
)

INNO_VERSION = "6.7.4"
INNO_HASH = "a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c"
REPOSITORY = "https://github.com/andre487/WindowsMegaProxy"


def version():
    value = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/Version")
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", value or ""):
        raise ValueError("Release version must be X.Y.Z.")
    if any(int(part) > 65535 for part in value.split(".")):
        raise ValueError("Release version exceeds Windows version limits.")
    return value


def merge(source, destination):
    for file in sorted(source.rglob("*")):
        if file.is_symlink():
            raise ValueError("Payload cannot contain symbolic links.")
        if not file.is_file() or file.suffix == ".pdb":
            continue
        target = destination / file.relative_to(source)
        if target.exists() and digest(target, "sha256") != digest(file, "sha256"):
            raise ValueError(f"Conflicting payload file: {target.name}")
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(file, target)


def archive(directory, output, *, prefix="", exclude=()):
    files = []
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as target:
        for file in sorted(directory.rglob("*")):
            relative = file.relative_to(directory).as_posix()
            if file.is_file() and not any(relative == item or relative.startswith(item + "/") for item in exclude):
                name = prefix + relative
                target.write(file, name)
                files.append({"path": name, "sha256": digest(file, "sha256"), "size": file.stat().st_size})
    return files


def signed_manifest(manifest, directory, key):
    public = key.public_key().public_bytes_raw()
    import hashlib
    manifest["signingKeyId"] = hashlib.sha256(public).hexdigest()
    data = (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    signature = key.sign(data)
    key.public_key().verify(signature, data)
    (directory / "update-manifest.json").write_bytes(data)
    (directory / "update-manifest.sig").write_bytes(signature)
    (directory / "update-public-key.txt").write_text(base64.b64encode(public).decode() + "\n", encoding="ascii")


def checksums(directory, names):
    (directory / "SHA256SUMS").write_text("".join(f"{digest(directory / name, 'sha256')}  {name}\n" for name in names), encoding="ascii", newline="\n")


def compiler():
    destination = ROOT / ".tools/inno"
    executable = destination / "ISCC.exe"
    if not executable.exists():
        installer = download(f"https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-{INNO_VERSION}.exe",
                             ROOT / f".tools/downloads/innosetup-{INNO_VERSION}.exe", INNO_HASH)
        sp.run([str(installer), "/PORTABLE=1", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CURRENTUSER", "/NOICONS", f"/DIR={destination}"], check=True)
    if not executable.is_file():
        raise RuntimeError("Inno Setup compiler is missing.")
    return executable


def core_installer_definitions(payload):
    definitions = {}
    for label, folder, http3 in (("StableCore", "sing-box", False), ("H3Core", "sing-box-h3", True)):
        core = core_package(http3=http3)
        definitions.update({label + "URL": core["url"], label + "SHA256": core["sha256"], label + "Directory": core["name"],
                            label + "ExecutableSHA256": digest(payload / "dependencies" / folder / "sing-box.exe", "sha256"),
                            label + "CronetSHA256": digest(payload / "dependencies" / folder / "libcronet.dll", "sha256")})
    return definitions


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", action="store_true", help="Use a temporary signing key; never publish as a production update.")
    parser.add_argument("--skip-build", action="store_true", help="Package existing production publish outputs.")
    args = parser.parse_args()
    app_version = version()
    commit = sp.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", commit):
        raise ValueError("Invalid source commit.")
    if args.preview:
        key = Ed25519PrivateKey.generate()
    else:
        encoded = os.environ.pop("UPDATE_SIGNING_KEY", "")
        if not encoded:
            raise RuntimeError("UPDATE_SIGNING_KEY is required. Use --preview for a local test package.")
        key = Ed25519PrivateKey.from_private_bytes(base64.b64decode(encoded, validate=True))
        pinned = base64.b64decode((ROOT / "assets/update-public-key.txt").read_text(encoding="ascii").strip(), validate=True)
        if key.public_key().public_bytes_raw() != pinned:
            raise RuntimeError("UPDATE_SIGNING_KEY does not match the public key pinned in the application.")
        if sp.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip():
            raise RuntimeError("Production releases require a clean checkout.")
    if not args.skip_build:
        build.main()
    output = ROOT / "artifacts" / ("release-preview" if args.preview else "release")
    output.mkdir(parents=True, exist_ok=True)
    cache = ROOT / ".tools/downloads"
    runtime = release()["windowsdesktop"]
    runtime_file = asset(runtime, ".exe")
    runtime_download = runtime_installer(cache)
    with Signer(preview=args.preview) as signer, tempfile.TemporaryDirectory(prefix="package-", dir=ROOT / "artifacts") as temporary:
        staging = Path(temporary)
        payload = staging / "payload"
        payload.mkdir()
        for name in ("app", "service"):
            source = ROOT / "artifacts" / name
            if not source.is_dir():
                raise RuntimeError("Production publish outputs are missing; run build.cmd.")
            merge(source, payload)
        launcher = ROOT / "artifacts/launcher"
        if not (launcher / "WindowsMegaProxy.Launcher.exe").is_file():
            raise RuntimeError("The current-user launcher is missing; run build.cmd.")
        merge(launcher, payload / "launcher")
        for name in ("WindowsMegaProxy.exe", "WindowsMegaProxy.Service.exe"):
            if not (payload / name).is_file():
                raise ValueError("Production executable missing from payload.")
            assembly = name.removesuffix(".exe")
            dependencies = json.loads((payload / f"{assembly}.deps.json").read_text(encoding="utf-8-sig"))
            if f"{assembly}/{app_version}" not in dependencies["libraries"]:
                raise ValueError("Published application version differs from the release version.")
            if not args.preview and commit.encode("ascii") not in (payload / f"{assembly}.dll").read_bytes():
                raise ValueError("Published application was built from a different commit.")
        if any(".Dev" in file.name or file.name in {"hostfxr.dll", "coreclr.dll", "python.exe"} for file in payload.rglob("*")):
            raise ValueError("Unexpected development or bundled runtime files.")
        shutil.copy2(ROOT / "LICENSE", payload / "LICENSE.txt")
        shutil.copy2(ROOT / "installer/THIRD-PARTY-NOTICES.txt", payload / "THIRD-PARTY-NOTICES.txt")
        shutil.copytree(ROOT / "installer/licenses", payload / "licenses")
        signer.sign_directory(payload)
        for name, http3 in (("sing-box", False), ("sing-box-h3", True)):
            install_core(payload / "dependencies" / name, cache, http3=http3)
        wintun = download("https://www.wintun.net/builds/wintun-0.14.1.zip", cache / "wintun-0.14.1.zip",
                          "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51")
        with zipfile.ZipFile(wintun) as upstream:
            license_file = next(name for name in upstream.namelist() if name.endswith("/LICENSE.txt"))
            (payload / "licenses/Wintun-LICENSE.txt").write_bytes(upstream.read(license_file))
        components = {}
        for component, directory, prefix, exclude, component_version in (
            ("app", payload, "", ("dependencies",), app_version),
            ("sing-box", payload / "dependencies/sing-box", "dependencies/sing-box/", (), CORE_VERSION),
            ("sing-box-h3", payload / "dependencies/sing-box-h3", "dependencies/sing-box-h3/", (), H3_CORE_VERSION),
        ):
            filename = f"WindowsMegaProxy-{app_version}-win-x64-{component}.zip"
            files = archive(directory, staging / filename, prefix=prefix, exclude=exclude)
            components[component] = {"version": component_version, "file": filename, "files": files}
        definitions = {
            "AppVersion": app_version, "VersionDirectory": f"{app_version}-{commit[:12]}",
            "RepositoryRoot": str(ROOT), "PayloadDirectory": str(payload), "ReleaseDirectory": str(staging),
            "RuntimeVersion": runtime["version"], "RuntimeURL": runtime_file["url"],
            "RuntimeSHA256": digest(runtime_download, "sha256"),
        }
        definitions.update(core_installer_definitions(payload))
        (staging / "package-config.iss").write_text("\n".join(f'#define {name} "{value}"' for name, value in definitions.items()) + "\n", encoding="utf-8-sig")
        script = (ROOT / "installer/WindowsMegaProxy.iss").read_text(encoding="utf-8-sig")
        script = script.replace("[Setup]\n", "[Setup]\nSignTool=megaproxy\nSignedUninstaller=yes\n", 1)
        (staging / "WindowsMegaProxy.iss").write_text(script, encoding="utf-8-sig")
        sp.run([str(compiler()), "/Qp", "/S" + signer.inno_tool(), str(staging / "WindowsMegaProxy.iss")],
               env=signer.environment, check=True)
        setup = f"WindowsMegaProxy-{app_version}-win-x64-setup.exe"
        components["installer"] = {"version": app_version, "file": setup}
        source = f"WindowsMegaProxy-{app_version}-source.zip"
        sp.run(["git", "archive", "--format=zip", f"--output={staging / source}", commit], cwd=ROOT, check=True)
        components["source"] = {"version": app_version, "file": source}
        for item in components.values():
            file = staging / item["file"]
            item.update(size=file.stat().st_size, sha256=digest(file, "sha256"),
                        url=f"{REPOSITORY}/releases/download/v{app_version}/{file.name}")
        manifest = {"schemaVersion": 1, "version": app_version, "commit": commit, "platform": "win-x64",
                    "preview": args.preview, "serviceProtocol": 3, "components": components,
                    "authenticodeCertificateSha256": signer.fingerprint,
                    "runtime": {"version": runtime["version"], "url": runtime_file["url"], "sha512": runtime_file["hash"]}}
        signed_manifest(manifest, staging, key)
        names = [item["file"] for item in components.values()] + ["update-manifest.json", "update-manifest.sig", "update-public-key.txt"]
        checksums(staging, names)
        for name in (*names, "SHA256SUMS"):
            shutil.copy2(staging / name, output / name)
    print(f"Package ready: {output}")


if __name__ == "__main__":
    cli(main)
