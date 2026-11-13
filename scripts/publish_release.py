"""Resume release packaging and publish only verified, hash-bound artifacts."""
import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess as sp
import tempfile
import time
from pathlib import Path

from cryptography import x509
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

import virus_total as vt
from authenticode import PUBLIC_CERTIFICATE

START = "<!-- megaproxy-virustotal -->"
END = "<!-- /megaproxy-virustotal -->"
METADATA = {"SHA256SUMS", "update-manifest.json", "update-manifest.sig", "update-public-key.txt"}
SETUP_ALIAS = "WindowsMegaProxy-setup.exe"


def has_current_authenticode(directory):
    expected = x509.load_pem_x509_certificate(PUBLIC_CERTIFICATE.read_bytes()).fingerprint(hashes.SHA256()).hex()
    manifest = json.loads((directory / "update-manifest.json").read_text(encoding="utf-8"))
    return manifest.get("authenticodeCertificateSha256") == expected


def gh(*args):
    result = sp.run(["gh", *args], capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("GitHub command failed: " + " ".join(args[:2]))
    return result.stdout


def release_state(repo, tag):
    result = sp.run(["gh", "api", f"repos/{repo}/releases/tags/{tag}"], capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        if "(HTTP 404)" not in result.stderr:
            raise RuntimeError("Cannot query the GitHub release; no assets were changed")
        # GitHub's tag endpoint omits drafts; the authenticated release list includes them.
        pages = json.loads(gh("api", "--paginate", "--slurp", f"repos/{repo}/releases?per_page=100"))
        matches = [item for page in pages for item in page if item.get("tag_name") == tag]
        if not matches:
            return None
        if len(matches) != 1:
            raise RuntimeError("Ambiguous GitHub release state; no assets were changed")
        state = matches[0]
    else:
        state = json.loads(result.stdout)
    if type(state.get("draft")) is not bool or state.get("tag_name") != tag:
        raise RuntimeError("Invalid GitHub release state")
    return state


def package_names(tag, *, bundled=None):
    version = tag[1:]
    prefix = f"WindowsMegaProxy-{version}"
    # Published releases through 0.6.0 have separate core assets and remain immutable.
    if bundled is None:
        bundled = tuple(map(int, version.split("."))) > (0, 6, 0)
    app = prefix + "-win-x64-app.zip"
    return {"app": prefix + "-win-x64-app.zip", "installer": prefix + "-win-x64-setup.exe",
            "sing-box": app if bundled else prefix + "-win-x64-sing-box.zip",
            "sing-box-h3": app if bundled else prefix + "-win-x64-sing-box-h3.zip",
            "source": prefix + "-source.zip"}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(directory, tag, commit):
    manifest_bytes = (directory / "update-manifest.json").read_bytes()
    manifest = json.loads(manifest_bytes)
    if (manifest.get("schemaVersion") != 1 or manifest.get("preview") is not False
            or manifest.get("version") != tag[1:] or manifest.get("commit") != commit):
        raise RuntimeError("Release manifest does not match the production tag and commit")
    public = base64.b64decode((directory / "update-public-key.txt").read_text().strip(), validate=True)
    if manifest.get("signingKeyId") != hashlib.sha256(public).hexdigest():
        raise RuntimeError("Release signing key ID differs from the public key")
    Ed25519PublicKey.from_public_bytes(public).verify((directory / "update-manifest.sig").read_bytes(), manifest_bytes)
    components = manifest["components"]
    names = package_names(tag, bundled=components["sing-box"]["file"] == components["app"]["file"])
    if set(manifest["components"]) != set(names):
        raise RuntimeError("Unexpected release components")
    for component, name in names.items():
        item = manifest["components"][component]
        path = directory / name
        if (item["file"] != name or item["sha256"] != digest(path) or item["size"] != path.stat().st_size
                or item["version"] != tag[1:] and component in ("app", "installer", "source")):
            raise RuntimeError("Release component differs from its signed manifest")
    files = set(names.values()) | METADATA
    expected = files - {"SHA256SUMS"}
    seen = set()
    for line in (directory / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)", line)
        if match and match[2] in {"VIRUSTOTAL.md", "VIRUSTOTAL.json"}:
            continue  # Older checksum lists may include optional diagnostics.
        if not match or match[2] not in expected or match[2] in seen:
            raise RuntimeError("Invalid release checksum entry")
        seen.add(match[2])
        if digest(directory / match[2]) != match[1]:
            raise RuntimeError("Release checksum mismatch")
    if not expected <= seen:
        raise RuntimeError("Release checksums are incomplete")
    return files


def download_release(state, repo, tag, directory):
    required = set(package_names(tag).values()) | METADATA
    available = {item["name"] for item in state["assets"]}
    if not required <= available:
        if state["draft"]:
            return False
        raise RuntimeError("Published release assets are incomplete; refusing to replace them")
    arguments = ["release", "download", tag, "--repo", repo, "--dir", str(directory)]
    for name in sorted(required):
        arguments += ["--pattern", name]
    gh(*arguments)
    return True


def restore(directory, repo, tag, commit, run_id=None):
    state = release_state(repo, tag)
    with tempfile.TemporaryDirectory(prefix="megaproxy-release-") as temporary:
        staging = Path(temporary)
        restored = False
        if state is not None:
            restored = download_release(state, repo, tag, staging)
            if restored:
                try:
                    verify(staging, tag, commit)
                except (OSError, ValueError, RuntimeError, KeyError, TypeError, InvalidSignature):
                    if not state["draft"]:
                        raise
                    restored = False
        if not restored and run_id:
            staging = Path(temporary) / "checkpoint"
            staging.mkdir()
            artifacts = json.loads(gh("api", f"repos/{repo}/actions/runs/{run_id}/artifacts?per_page=100"))["artifacts"]
            existing = {item["name"] for item in artifacts if not item["expired"]}
            if "release-packages" in existing:
                gh("run", "download", str(run_id), "--repo", repo, "--name", "release-packages", "--dir", str(staging))
                verify(staging, tag, commit)
                restored = True
        if restored and (state is None or state["draft"]) and not has_current_authenticode(staging):
            return False
        if not restored:
            return False
        directory.mkdir(parents=True, exist_ok=True)
        for name in verify(staging, tag, commit):
            shutil.copy2(staging / name, directory / name)
        print("Restored original release files; rebuilding is unnecessary.")
        return True


def release_notes(body, report=None):
    body = re.sub(re.escape(START) + ".*?" + re.escape(END), "", body, flags=re.S).rstrip()
    if report is not None:
        body += f"\n\n{START}\n{report.rstrip()}\n{END}"
    return body + "\n"


def publish(directory, repo, tag, commit, notes_path, *, virustotal=False):
    names = verify(directory, tag, commit)
    state = release_state(repo, tag)
    if (state is None or state["draft"]) and not has_current_authenticode(directory):
        raise RuntimeError("New releases require packages signed with the current Windows certificate.")
    with tempfile.TemporaryDirectory(prefix="megaproxy-publish-") as temporary:
        temporary = Path(temporary)
        if state is not None and not state["draft"]:
            original = temporary / "original"
            original.mkdir()
            download_release(state, repo, tag, original)
            verify(original, tag, commit)
            if any(digest(original / name) != digest(directory / name) for name in names):
                raise RuntimeError("Published release differs from these packages; refusing to replace assets")
            body = state.get("body") or ""
        else:
            body = notes_path.read_text(encoding="utf-8")
        report = None
        if virustotal:
            rows = vt.scan_artifacts(directory, temporary / "virustotal", max_detections=10)
            report = vt.report_text(rows, complete=True)
        notes = temporary / "notes.md"
        notes.write_text(release_notes(body, report), encoding="utf-8")
        if state is None:
            gh("release", "create", tag, "--repo", repo, "--verify-tag", "--draft", "--title", tag, "--notes-file", str(notes))
        if state is None or state["draft"]:
            # Only unpublished drafts may replace files after an interrupted upload/build.
            for delay in (2, 4, 8, 16, 30):
                time.sleep(delay)
                current = release_state(repo, tag)
                if current is not None:
                    break
            if current is None or not current["draft"]:
                raise RuntimeError("Release is no longer a draft; refusing asset replacement")
            # The permalink is byte-identical to the installer verified against the signed manifest above.
            alias = temporary / SETUP_ALIAS
            shutil.copyfile(directory / package_names(tag)["installer"], alias)
            gh("release", "upload", tag, *[str(directory / name) for name in sorted(names)], str(alias), "--repo", repo, "--clobber")
            gh("release", "edit", tag, "--repo", repo, "--notes-file", str(notes), "--draft=false")
        else:
            gh("release", "edit", tag, "--repo", repo, "--notes-file", str(notes))
            print("Published assets preserved; release notes are up to date.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("restore", "publish"))
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts/release"))
    parser.add_argument("--virustotal", action="store_true", help="Scan both release files; allow at most 10 detections per file")
    args = parser.parse_args()
    tag = os.environ["RELEASE_TAG"]
    repo = os.environ["GITHUB_REPOSITORY"]
    if not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag) or not re.fullmatch(r"[\w.-]+/[\w.-]+", repo):
        raise RuntimeError("Invalid release tag or repository")
    commit = gh("api", f"repos/{repo}/commits/{tag}")
    commit = json.loads(commit)["sha"]
    if not re.fullmatch(r"[a-f0-9]{40}", commit):
        raise RuntimeError("Invalid release commit")
    if args.operation == "restore":
        reused = restore(args.artifacts, repo, tag, commit, os.environ.get("GITHUB_RUN_ID"))
        if output := os.environ.get("GITHUB_OUTPUT"):
            with open(output, "a", encoding="utf-8") as stream:
                stream.write(f"reused={str(reused).lower()}\n")
    else:
        publish(args.artifacts, repo, tag, commit, Path(__file__).resolve().parent.parent / "release-notes" / (tag + ".md"), virustotal=args.virustotal)


if __name__ == "__main__":
    try:
        main()
    except RuntimeError as error:
        raise SystemExit(str(error)) from None
    except Exception:
        # Network payloads, credentials and upload URLs must never enter action logs.
        raise SystemExit("Release operation failed; publication stopped. Verify original assets, signatures and checksums.") from None
