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
from cryptography.hazmat.primitives.serialization import Encoding

import build
from authenticode import Signer
from common import ROOT, cli, core_mirrors, digest, download, extract, github_asset_url
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


def core_installer_definitions(payload, *, legacy=False):
    if not legacy:
        validate_native_payload(payload)
        return {}
    definitions = {}
    for label, folder, http3 in (("StableCore", "sing-box", False), ("H3Core", "sing-box-h3", True)):
        core = core_package(http3=http3)
        definitions.update({label + "URL": core["url"], label + "SHA256": core["sha256"], label + "Directory": core["name"],
                            label + "ApiURL": core.get("apiUrl") or github_asset_url(core["url"]),
                            label + "Mirrors": "|".join(core_mirrors(core)),
                            label + "ExecutableSHA256": digest(payload / "dependencies" / folder / "sing-box.exe", "sha256"),
                            label + "CronetSHA256": digest(payload / "dependencies" / folder / "libcronet.dll", "sha256")})
    return definitions


def validate_native_payload(payload):
    for folder, variant in (("sing-box", "Stable"), ("sing-box-h3", "Experimental")):
        for name in ("WindowsMegaProxy.Core.exe", f"MegaProxy.Core.{variant}.dll"):
            if not (payload / "dependencies" / folder / name).is_file():
                raise ValueError("Native core payload is incomplete.")
    if any(path.name.lower() in ("sing-box.exe", "sing-box-dev.exe", "libcronet.dll") for path in payload.rglob("*")):
        raise ValueError("Legacy sing-box binaries must not be packaged.")


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
    wix_installer = (ROOT / "installer/wix/Bundle.wxs").exists()
    installer_source = "" if wix_installer else (ROOT / "installer/WindowsMegaProxy.iss").read_text(encoding="utf-8-sig")
    # Only release_package.py rebuilding a separate historical checkout may package pre-DLL cores.
    legacy_core = ROOT.resolve() != Path(__file__).resolve().parents[1] and not (ROOT / "native/core/go.mod").exists()
    legacy_runtime = ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/RuntimeFrameworkVersion") is None
    runtime = release()["windowsdesktop"] if legacy_runtime else {"version": ET.parse(ROOT / "Directory.Build.props").findtext("PropertyGroup/RuntimeFrameworkVersion"), "bundled": True}
    runtime_file = asset(runtime, ".exe") if legacy_runtime else None
    runtime_download = runtime_installer(cache) if legacy_runtime else None
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
        for name in ("WindowsMegaProxy.exe", "WindowsMegaProxy.Service.exe", "launcher/WindowsMegaProxy.Launcher.exe"):
            if not (payload / name).is_file():
                raise ValueError("Production executable missing from payload.")
            executable = payload / name
            assembly = executable.stem
            dependency_file = executable.with_suffix(".deps.json")
            if dependency_file.exists():
                dependencies = json.loads(dependency_file.read_text(encoding="utf-8-sig"))
                if f"{assembly}/{app_version}" not in dependencies["libraries"]:
                    raise ValueError("Published application version differs from the release version.")
                information = executable.with_suffix(".dll").read_bytes()
                if not args.preview and commit.encode("ascii") not in information:
                    raise ValueError("Published application was built from a different commit.")
            else:
                product = build.product_version(payload / name)
                if product.split("+", 1)[0] != app_version:
                    raise ValueError("Published application version differs from the release version.")
                if not args.preview and product.split("+", 1)[-1] != commit:
                    raise ValueError("Published application was built from a different commit.")
        if any(".Dev" in file.name or file.name == "python.exe" for file in payload.rglob("*")):
            raise ValueError("Unexpected development files.")
        shutil.copy2(ROOT / "src/LICENSE" if (ROOT / "src/LICENSE").exists() else ROOT / "LICENSE", payload / "LICENSE.txt")
        if (ROOT / "LICENSING.md").exists():
            shutil.copy2(ROOT / "LICENSING.md", payload / "LICENSING.md")
        shutil.copy2(ROOT / "installer/THIRD-PARTY-NOTICES.txt", payload / "THIRD-PARTY-NOTICES.txt")
        shutil.copytree(ROOT / "installer/licenses", payload / "licenses")
        if not legacy_core:
            (payload / "licenses/Cronet-Go-LICENSE.txt").unlink(missing_ok=True)
        if (ROOT / "src/WindowsMegaProxy.Setup/LICENSE").exists():
            shutil.copy2(ROOT / "src/WindowsMegaProxy.Setup/LICENSE", payload / "licenses/Setup-MIT.txt")
        shutil.copy2(ROOT / "assets/branding/IDENTITY-LICENSE", payload / "licenses/Artwork-CC0.txt")
        if not legacy_runtime:
            for package, filename, output_name in (
                ("microsoft.netcore.app.runtime.win-x64", "LICENSE.TXT", "dotnet-LICENSE.txt"),
                ("microsoft.netcore.app.runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "dotnet-THIRD-PARTY-NOTICES.txt"),
                ("microsoft.windowsdesktop.app.runtime.win-x64", "LICENSE", "WindowsDesktop-LICENSE.txt"),
            ):
                shutil.copy2(ROOT / ".tools/nuget" / package / runtime["version"] / filename, payload / "licenses" / output_name)
        publisher = staging / "core-publisher.pem"
        publisher.write_bytes(signer.certificate.public_bytes(Encoding.PEM))
        for name, http3 in (("sing-box", False), ("sing-box-h3", True)):
            if not legacy_core:
                import build_core
                build_core.install(payload / "dependencies" / name, experimental=http3, publisher=publisher)
            else:
                # Previous release tags still use the upstream executables during upgrade tests.
                install_core(payload / "dependencies" / name, cache, http3=http3)
        if not legacy_core:
            validate_native_payload(payload)
        signer.sign_directory(payload)
        runtime_files = []
        if wix_installer or '#include "Runtime.iss"' in installer_source:
            import runtime_payload
            runtime_files, runtime = runtime_payload.detach(payload, staging, ROOT / ".tools/nuget", runtime["version"], legacy=not wix_installer)
        wintun = download("https://www.wintun.net/builds/wintun-0.14.1.zip", cache / "wintun-0.14.1.zip",
                          "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51")
        with zipfile.ZipFile(wintun) as upstream:
            license_file = next(name for name in upstream.namelist() if name.endswith("/LICENSE.txt"))
            (payload / "licenses/Wintun-LICENSE.txt").write_bytes(upstream.read(license_file))
        components = {}
        for component, directory, prefix, exclude, component_version in (
            ("app", payload, "", ("dependencies",) if legacy_runtime else (), app_version),
            ("sing-box", payload / "dependencies/sing-box", "dependencies/sing-box/", (), CORE_VERSION),
            ("sing-box-h3", payload / "dependencies/sing-box-h3", "dependencies/sing-box-h3/", (), H3_CORE_VERSION),
        ):
            filename = f"WindowsMegaProxy-{app_version}-win-x64-{component}.zip"
            files = archive(directory, staging / filename, prefix=prefix, exclude=exclude)
            if component == "app":
                # Installed file inventory includes downloaded runtime files for old boot-update workers.
                files.extend(runtime_files)
            components[component] = {"version": component_version, "file": filename, "files": files}
        definitions = {
            "AppVersion": app_version, "VersionDirectory": f"{app_version}-{commit[:12]}",
            "RepositoryRoot": str(ROOT), "PayloadDirectory": str(payload), "ReleaseDirectory": str(staging),
            "RuntimeVersion": runtime["version"], "RuntimeURL": runtime_file["url"] if runtime_file else "",
            "RuntimeSHA256": digest(runtime_download, "sha256") if runtime_download else "",
            "H3CoreVersion": H3_CORE_VERSION,
        }
        definitions.update(core_installer_definitions(payload, legacy=legacy_core))
        if wix_installer:
            import wix_package
            wix_package.build_installer(staging, payload, definitions, runtime, signer)
        else:
            (staging / "package-config.iss").write_text("\n".join(f'#define {name} "{value}"' for name, value in definitions.items()) + "\n", encoding="utf-8-sig")
            script = (ROOT / "installer/WindowsMegaProxy.iss").read_text(encoding="utf-8-sig")
            script = script.replace("[Setup]\n", "[Setup]\nSignTool=megaproxy\nSignedUninstaller=yes\n", 1)
            helper = Path(__file__).resolve().parent.parent / "installer/legacy/Downloads.iss"
            shutil.copy2(helper, staging / "Downloads.iss")
            if '#include "Runtime.iss"' in script:
                shutil.copy2(ROOT / "installer/Runtime.iss", staging / "Runtime.iss")
            if '#include "Winget.iss"' in script:
                shutil.copy2(ROOT / "installer/Winget.iss", staging / "Winget.iss")
                h3_script = (ROOT / "installer/Http3Core.iss").read_text(encoding="utf-8-sig")
                h3_script = h3_script.replace("[Setup]\n", "[Setup]\nSignTool=megaproxy\n", 1)
                (staging / "Http3Core.iss").write_text(h3_script, encoding="utf-8-sig")
                sp.run([str(compiler()), "/Qp", "/S" + signer.inno_tool(), str(staging / "Http3Core.iss")], env=signer.environment, check=True)
                app = components["app"]
                app["files"] = archive(payload, staging / app["file"], exclude=("dependencies",))
            if "{#StableCoreURL}" in script and '#include "Downloads.iss"' not in script:
                script = script.replace("[Code]\n", '[Code]\n#include "Downloads.iss"\n', 1)
                legacy = ("      Download.Add('{#StableCoreURL}', 'sing-box-stable.zip', '{#StableCoreSHA256}');\n"
                          "      Download.Add('{#H3CoreURL}', 'sing-box-h3.zip', '{#H3CoreSHA256}');\n"
                          "      Download.Show;\n      Download.Download;")
                script = script.replace(legacy, "      Download.Show;\n"
                    "      DownloadCore(Download, '{#StableCoreURL}', '{#StableCoreApiURL}', '{#StableCoreMirrors}', 'sing-box-stable.zip', '{#StableCoreSHA256}');\n"
                    "      DownloadCore(Download, '{#H3CoreURL}', '{#H3CoreApiURL}', '{#H3CoreMirrors}', 'sing-box-h3.zip', '{#H3CoreSHA256}');")
            (staging / "WindowsMegaProxy.iss").write_text(script, encoding="utf-8-sig")
            checks = ROOT / "artifacts/installer-checks/download-rules"
            checks.mkdir(parents=True, exist_ok=True)
            (checks / "result.txt").unlink(missing_ok=True)
            source_root = Path(__file__).resolve().parent.parent
            shutil.copy2(source_root / "installer/legacy/InstallerDownloads.iss", staging / "InstallerDownloads.iss")
            (staging / "check-config.iss").write_text(f'#define CheckOutputDirectory "{checks}"\n', encoding="utf-8-sig")
            sp.run([str(compiler()), "/Qp", str(staging / "InstallerDownloads.iss")], check=True)
            result = sp.run([str(checks / "installer-download-checks.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES",
                             f"/LOG={checks.parent / 'download-rules.log'}"], timeout=30)
            # InitializeSetup returns False after the checks, so Inno exits before installation with code 1.
            if result.returncode != 1 or (checks / "result.txt").read_text() != "passed":
                raise RuntimeError("Installer download checks failed; no installation was attempted.")
            if runtime_files:
                runtime_checks = staging / "runtime-checks"
                runtime_checks.mkdir()
                for name in ("Downloads.iss", "Runtime.iss", "check-config.iss"):
                    shutil.copy2(staging / name, runtime_checks / name)
                (runtime_checks / "runtime-files.iss").write_text("procedure InitializeRuntimeFiles; begin end;\n", encoding="ascii")
                with zipfile.ZipFile(runtime_checks / "runtime-fixture.zip", "w") as fixture:
                    fixture.writestr("runtimes/win-x64/native/coreclr.dll", b"runtime fixture")
                shutil.copy2(source_root / "installer/legacy/InstallerRuntime.iss", runtime_checks)
                (checks / "runtime-result.txt").unlink(missing_ok=True)
                sp.run([str(compiler()), "/Qp", str(runtime_checks / "InstallerRuntime.iss")], check=True)
                result = sp.run([str(checks / "installer-runtime-checks.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], timeout=30)
                if result.returncode != 1 or (checks / "runtime-result.txt").read_text() != "passed":
                    raise RuntimeError("Installer runtime preparation checks failed; no installation was attempted.")
            sp.run([str(compiler()), "/Qp", "/S" + signer.inno_tool(), str(staging / "WindowsMegaProxy.iss")],
                   env=signer.environment, check=True)
        setup = f"WindowsMegaProxy-{app_version}-win-x64-setup.exe"
        components["installer"] = {"version": app_version, "file": setup}
        if wix_installer:
            components["installer"]["format"] = "wix-burn"
        source = f"WindowsMegaProxy-{app_version}-source.zip"
        sp.run(["git", "archive", "--format=zip", f"--output={staging / source}", commit], cwd=ROOT, check=True)
        if not legacy_core and wix_installer:
            from source_bundle import append_dependencies
            append_dependencies(staging / source, root=ROOT)
        components["source"] = {"version": app_version, "file": source}
        for item in components.values():
            file = staging / item["file"]
            item.update(size=file.stat().st_size, sha256=digest(file, "sha256"),
                        url=f"{REPOSITORY}/releases/download/v{app_version}/{file.name}")
        manifest = {"schemaVersion": 1, "version": app_version, "commit": commit, "platform": "win-x64",
                    "preview": args.preview, "serviceProtocol": 3, "components": components,
                    "authenticodeCertificateSha256": signer.fingerprint,
                    "runtime": {"version": runtime["version"], "url": runtime_file["url"], "sha512": runtime_file["hash"]} if legacy_runtime else runtime}
        signed_manifest(manifest, staging, key)
        names = [item["file"] for item in components.values()] + ["update-manifest.json", "update-manifest.sig", "update-public-key.txt"]
        checksums(staging, names)
        for name in (*names, "SHA256SUMS"):
            shutil.copy2(staging / name, output / name)
    print(f"Package ready: {output}")


if __name__ == "__main__":
    cli(main)
