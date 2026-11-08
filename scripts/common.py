"""Shared paths and standard-library helpers for the Windows development scripts."""

import hashlib
import json
import os
import re
import shutil
import subprocess as sp
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SDK_VERSION = "10.0.403"


def app_name(dev=False):
    return "WindowsMegaProxy.Dev" if dev else "WindowsMegaProxy"


def artifact_root(dev=False):
    return ROOT / "artifacts/dev" if dev else ROOT / "artifacts"


def dotnet_path():
    local = ROOT / ".tools/dotnet/dotnet.exe"
    return str(local) if local.exists() else shutil.which("dotnet")


def dotnet(*args):
    executable = dotnet_path()
    if not executable:
        raise RuntimeError(".NET SDK is missing. Run setup.cmd first.")
    env = os.environ.copy()
    env.update(
        DOTNET_CLI_HOME=str(ROOT / ".tools/cli"),
        DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_NOLOGO="1",
        DOTNET_CLI_UI_LANGUAGE="en-US",
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="0",
        NUGET_PACKAGES=str(ROOT / ".tools/nuget"),
    )
    if Path(executable).parent == ROOT / ".tools/dotnet":
        env["DOTNET_ROOT"] = str(Path(executable).parent)
    return sp.run([executable, *args], cwd=ROOT, env=env, stdout=sys.stdout, stderr=sys.stderr, check=True)


class DownloadHttpError(RuntimeError):
    def __init__(self, url, status, attempts):
        self.url, self.status = url, status
        super().__init__(f"Download failed from {urllib.parse.urlsplit(url).hostname}: HTTP {status} after {attempts} attempt(s).")


def open_download(url, *, timeout=60, headers=None):
    host = urllib.parse.urlsplit(url).hostname or "unknown host"
    request = urllib.request.Request(url, headers=headers) if headers else url
    for attempt in range(4):
        try:
            response = urllib.request.urlopen(request, timeout=timeout)
            if not response.geturl().startswith("https://"):
                response.close()
                raise RuntimeError("Refusing an insecure download redirect.")
            return response
        except urllib.error.HTTPError as error:
            status = error.code
            response_headers = error.headers or {}
            details = "; ".join(f"{name}: {response_headers[name]}" for name in
                                ("Server", "Via", "X-Cache", "X-Azure-Ref", "X-Ms-Request-Id", "X-GitHub-Request-Id",
                                 "X-GitHub-Edge-Region", "Content-Type", "Retry-After") if response_headers.get(name))
            try:
                body = error.read(2048).decode("utf-8", errors="replace")
            except (OSError, TimeoutError):
                body = "Response body unavailable."
            finally:
                error.close()
            body = re.sub(r"<[^>]+>", " ", body)
            body = " ".join("".join(character if character.isprintable() else " " for character in body).split())
            parsed = urllib.parse.urlsplit(url)
            source = urllib.parse.urlunsplit((parsed.scheme, parsed.hostname or "", parsed.path, "", ""))
            print(f"Download from {source}: HTTP {status}. {details}\nResponse: {body}", flush=True)
            if not 500 <= status < 600 or attempt == 3:
                raise DownloadHttpError(url, status, attempt + 1) from None
            delay = 2 ** attempt
            print(f"Retrying {host} in {delay} second(s), attempt {attempt + 2}/4.", flush=True)
            time.sleep(delay)


def github_asset_url(url):
    match = re.fullmatch(r"https://github.com/([\w.-]+)/([\w.-]+)/releases/download/([^/?#]+)/([^/?#]+)", url)
    if match is None:
        return None
    owner, repository, tag, filename = match.groups()
    metadata_url = f"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}"
    with open_download(metadata_url, headers={"Accept": "application/vnd.github+json"}) as response:
        release = json.load(response)
    for asset in release.get("assets", []):
        if asset.get("name") == urllib.parse.unquote(filename) and asset.get("browser_download_url") == url:
            endpoint = asset.get("url", "")
            if re.fullmatch(rf"https://api\.github\.com/repos/{re.escape(owner)}/{re.escape(repository)}/releases/assets/[0-9]+", endpoint):
                return endpoint
            raise RuntimeError("GitHub returned an untrusted release asset endpoint.")
    raise RuntimeError("The official GitHub release does not contain the requested asset.")


def download(url, path, expected, algorithm="sha256", *, headers=None):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and digest(path, algorithm) == expected.lower():
        return path
    temporary = path.with_name(path.name + ".download")
    try:
        with open_download(url, headers=headers) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        if digest(temporary, algorithm) != expected.lower():
            raise RuntimeError(f"Checksum mismatch: {path.name}")
        temporary.replace(path)
    except DownloadHttpError as error:
        endpoint = github_asset_url(url) if 500 <= error.status < 600 else None
        if endpoint is None:
            raise
        print("Retrying the same release asset through the official GitHub API; the pinned hash is unchanged.", flush=True)
        return download(endpoint, path, expected, algorithm, headers={"Accept": "application/octet-stream"})
    finally:
        temporary.unlink(missing_ok=True)
    return path


def digest(path, algorithm):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def extract(archive, destination):
    destination = Path(destination).resolve()
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            if not (destination / item.filename).resolve().is_relative_to(destination):
                raise RuntimeError("Archive entry escapes its destination.")
        source.extractall(destination)


def cli(main):
    try:
        main()
    except (OSError, RuntimeError, sp.SubprocessError, ValueError) as error:
        print(f"Error: {error}", file=sys.stderr)
        raise SystemExit(1)
