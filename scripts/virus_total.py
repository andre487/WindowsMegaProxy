"""Scan the installer and application archive, preserving hash-bound VirusTotal reports."""

import argparse
import hashlib
import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

API = "https://www.virustotal.com/api/v3/"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError(
            "VirusTotal unexpectedly redirected an authenticated request"
        )


class Client:
    def __init__(self, key):
        if not re.fullmatch(r"[0-9a-fA-F]{64}", key):
            raise RuntimeError("VIRUSTOTAL_API_KEY is missing or invalid")
        self.key = key
        self.opener = urllib.request.build_opener(NoRedirect())
        self.next_request = 0.0
        self.deadline = time.monotonic() + 1800

    def request(self, url, body=None, content_type=None):
        parsed = urllib.parse.urlsplit(url)
        if (
            parsed.scheme != "https"
            or parsed.hostname not in ("www.virustotal.com", "bigfiles.virustotal.com")
            or parsed.port not in (None, 443)
            or parsed.username
            or parsed.password
        ):
            raise RuntimeError("VirusTotal returned an untrusted upload URL")
        delay = max(0, self.next_request - time.monotonic())
        if time.monotonic() + delay >= self.deadline:
            raise RuntimeError("VirusTotal scan deadline exceeded (30 minutes)")
        time.sleep(delay)
        self.next_request = time.monotonic() + 16  # Public API: at most 4/minute.
        headers = {"x-apikey": self.key}
        if content_type:
            headers["Content-Type"] = content_type
        req = urllib.request.Request(url, data=body, headers=headers)
        try:
            with self.opener.open(
                req, timeout=min(120, self.deadline - time.monotonic())
            ) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            error.close()
            raise RuntimeError(f"VirusTotal API failed (HTTP {error.code})") from None
        except (urllib.error.URLError, TimeoutError):
            raise RuntimeError("VirusTotal network request failed") from None

    def scan(self, path):
        size = path.stat().st_size
        if not 0 < size <= 650 * 1024 * 1024:
            raise RuntimeError("Artifact size is outside VirusTotal upload limits")
        content = path.read_bytes()
        if len(content) != size:
            raise RuntimeError("Artifact changed during VirusTotal upload preparation")
        digest = hashlib.sha256(content).hexdigest()
        url = API + "files"
        if len(content) >= 32 * 1024 * 1024:
            url = self.request(API + "files/upload_url")["data"]
        boundary = uuid.uuid4().hex
        body = (
            (
                f'--{boundary}\r\nContent-Disposition: form-data; name="file"; '
                f'filename="{path.name}"\r\nContent-Type: application/octet-stream\r\n\r\n'
            ).encode()
            + content
            + f"\r\n--{boundary}--\r\n".encode()
        )
        analysis_id = self.request(
            url, body, f"multipart/form-data; boundary={boundary}"
        )["data"]["id"]
        while True:
            response = self.request(
                API + "analyses/" + urllib.parse.quote(analysis_id, safe="")
            )
            attributes = response["data"]["attributes"]
            if attributes["status"] == "completed":
                break
            if attributes["status"] not in ("queued", "in-progress"):
                raise RuntimeError("VirusTotal returned an unknown analysis status")
        reported = response.get("meta", {}).get("file_info", {}).get("sha256")
        if reported is not None and reported != digest:
            raise RuntimeError("VirusTotal analysis hash differs from uploaded artifact")
        stats = attributes["stats"]
        if (
            not stats
            or any(type(value) is not int or value < 0 for value in stats.values())
            or not all(
                key in stats for key in ("malicious", "suspicious", "undetected")
            )
            or sum(
                stats.get(k, 0)
                for k in ("malicious", "suspicious", "harmless", "undetected")
            )
            == 0
        ):
            raise RuntimeError("VirusTotal completed without usable engine verdicts")
        return {
            "file": path.name,
            "sha256": digest,
            "analysis_id": analysis_id,
            "stats": stats,
            "url": f"https://www.virustotal.com/gui/file/{digest}",
        }


def targets(directory):
    manifest = json.loads((directory / "update-manifest.json").read_text(encoding="utf-8"))
    paths = []
    for component, suffix in (("installer", "setup.exe"), ("app", "app.zip")):
        item = manifest["components"][component]
        name = item["file"]
        if not re.fullmatch(r"WindowsMegaProxy-\d+\.\d+\.\d+-win-x64-" + re.escape(suffix), name):
            raise RuntimeError("Invalid VirusTotal artifact filename")
        path = directory / name
        if path.is_symlink():
            raise RuntimeError("VirusTotal artifacts cannot be symbolic links")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != item["sha256"]:
            raise RuntimeError("VirusTotal artifact differs from the update manifest")
        paths.append(path)
    return paths


def valid_row(row, path):
    try:
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        stats = row["stats"]
        return (row["file"] == path.name and row["sha256"] == digest
                and row["url"] == f"https://www.virustotal.com/gui/file/{digest}"
                and bool(stats) and all(type(value) is int and value >= 0 for value in stats.values())
                and all(key in stats for key in ("malicious", "suspicious", "undetected"))
                and sum(stats.get(key, 0) for key in ("malicious", "suspicious", "harmless", "undetected")) > 0)
    except (KeyError, TypeError, AttributeError):
        return False


def write_report(directory, rows, complete):
    report = "## VirusTotal\n\n"
    if not complete:
        report += "**Scan incomplete; release publication blocked.**\n\n"
    report += "| File | Malicious | Suspicious | Report |\n| --- | --- | --- | --- |\n"
    for row in rows:
        stats = row["stats"]
        report += f"| {row['file']} | {stats['malicious']} | {stats['suspicious']} | [SHA-256 report]({row['url']}) |\n"
    report += "\nCompleted engine verdicts are a point-in-time check, not a guarantee of safety. See each report for unsupported engines, failures and timeouts.\n"
    (directory / "VIRUSTOTAL.md").write_text(report, encoding="utf-8")
    (directory / "VIRUSTOTAL.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary, "a", encoding="utf-8") as output:
            output.write(report)


def scan_artifacts(directory):
    paths = targets(directory)
    try:
        cached = json.loads((directory / "VIRUSTOTAL.json").read_text(encoding="utf-8"))
        if not isinstance(cached, list):
            cached = []
    except (FileNotFoundError, ValueError):
        cached = []
    client = None
    rows = []
    complete = False
    try:
        for path in paths:
            row = next((row for row in cached if valid_row(row, path)
                        and not row["stats"]["malicious"] and not row["stats"]["suspicious"]), None)
            if row is None:
                client = client or Client(os.environ.get("VIRUSTOTAL_API_KEY", "").strip())
                print(f"Scanning {path.name}", flush=True)
                row = client.scan(path)
                if not valid_row(row, path):
                    raise RuntimeError("VirusTotal returned an invalid artifact report")
            else:
                print(f"Reusing the SHA-256 report for {path.name}", flush=True)
            rows.append(row)
        complete = True
    finally:
        write_report(directory, rows, complete)
    if any(row["stats"]["malicious"] or row["stats"]["suspicious"] for row in rows):
        raise RuntimeError("VirusTotal detections require review; publication blocked")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=Path("artifacts/release"))
    args = parser.parse_args()
    try:
        scan_artifacts(args.artifacts)
    except (RuntimeError, KeyError, TypeError, ValueError, OSError, AttributeError) as error:
        message = str(error) if isinstance(error, RuntimeError) else "VirusTotal operation failed"
        raise SystemExit(message) from None


if __name__ == "__main__":
    main()
