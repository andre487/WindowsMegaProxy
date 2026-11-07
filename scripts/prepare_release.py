"""Prepare a reviewable version/notes PR; publishing is handled by release.yml."""
import json
import os
import re
import subprocess as sp
import tempfile
from pathlib import Path

from common import ROOT, cli
from package import version
from publish_release import gh
from release_notes import generate_notes


def main():
    if not os.environ.get("GH_TOKEN"):
        raise RuntimeError("RELEASE_BOT_TOKEN is required to create a PR that triggers CI.")
    target = os.environ.get("RELEASE_VERSION", "")
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", target):
        raise ValueError("Expected X.Y.Z.")
    if any(int(part) > 65535 for part in target.split(".")):
        raise ValueError("Version exceeds Windows limits.")
    current = version()
    if tuple(map(int, target.split("."))) < tuple(map(int, current.split("."))):
        raise ValueError("Release version cannot go backwards.")
    existing = existing_release_pr(target)
    if existing:
        print(f"Resuming release preparation PR #{existing}.")
        return
    if sp.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip():
        raise RuntimeError("Release preparation requires a clean checkout.")
    english, russian = generate_notes(target)
    tag = "v" + target
    branch = "prepare-release/" + tag
    sp.run(["git", "switch", "-c", branch], cwd=ROOT, check=True)
    props = ROOT / "Directory.Build.props"
    text = props.read_text(encoding="utf-8")
    text = text.replace(f"<Version>{current}</Version>", f"<Version>{target}</Version>")
    text = text.replace(f"<Version>{current}-dev</Version>", f"<Version>{target}-dev</Version>")
    props.write_text(text, encoding="utf-8")
    notes = ROOT / f"release-notes/{tag}.md"
    notes.parent.mkdir(exist_ok=True)
    notes.write_text(f"# {tag}\n\n## English\n\n{english}\n\n## Русский\n\n{russian}\n", encoding="utf-8")
    sp.run(["git", "add", "Directory.Build.props", notes.relative_to(ROOT).as_posix()], cwd=ROOT, check=True)
    sp.run(["git", "-c", "user.name=github-actions[bot]", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com", "commit", "-m", f"Prepare {tag}"], cwd=ROOT, check=True)
    sp.run(["gh", "auth", "setup-git"], cwd=ROOT, check=True)
    sp.run(["git", "push", "origin", branch], cwd=ROOT, check=True)
    with tempfile.TemporaryDirectory() as temporary:
        body = Path(temporary) / "body.md"
        body.write_text(f"Prepare {tag}. The requested release workflow waits for full CI, squash merges this PR and tags the merged commit `{tag}` to publish.\n\n" + notes.read_text(encoding="utf-8"), encoding="utf-8")
        sp.run(["gh", "pr", "create", "--base", "main", "--head", branch, "--title", tag, "--body-file", str(body)], cwd=ROOT, check=True)


def existing_release_pr(target):
    rows = json.loads(gh("pr", "list", "--head", "prepare-release/v" + target, "--base", "main", "--state", "all", "--json", "number,state"))
    if not rows:
        return None
    if len(rows) != 1 or rows[0]["state"] not in ("OPEN", "MERGED"):
        raise RuntimeError("Release preparation branch has an ambiguous or closed PR.")
    return rows[0]["number"]


if __name__ == "__main__":
    cli(main)
