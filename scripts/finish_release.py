"""Require exact PR CI before squash merging and creating an immutable release tag."""

import json
import os
import re
import subprocess as sp
import time

from common import ROOT, cli
from publish_release import gh


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def git(*args):
    return sp.check_output(["git", *args], cwd=ROOT, text=True, encoding="utf-8").strip()


def api(path, method="GET", **fields):
    args = ["api", f"repos/{os.environ['GITHUB_REPOSITORY']}/{path}", "--method", method]
    for key, value in fields.items():
        args.extend(["-f", f"{key}={value}"])
    return json.loads(gh(*args))


def check_pr(pr, target, head):
    repo = os.environ["GITHUB_REPOSITORY"]
    require(pr["head"]["sha"] == head, "Release PR changed; refusing automatic merge or tag.")
    require(pr["head"]["ref"] == "prepare-release/v" + target and pr["base"]["ref"] == "main"
            and pr["head"]["repo"]["full_name"] == pr["base"]["repo"]["full_name"] == repo,
            "Release PR must belong to this repository and target main.")
    require(not pr["draft"] and (pr["state"] == "open" or pr["merged"]), "Release PR is draft or closed without merging.")


def release_tag(target):
    ref = "refs/tags/v" + target
    existing = git("ls-remote", "--tags", "origin", ref, ref + "^{}")
    refs = dict(line.split()[::-1] for line in existing.splitlines())
    return refs.get(ref + "^{}", refs.get(ref))


def check_release_tag(target, pr):
    existing = release_tag(target)
    require(not existing or pr["merged"] and existing == pr["merge_commit_sha"],
            "Release tag already points elsewhere; it will not be moved. Choose a new version.")
    return existing


def full_ci(run, jobs, number, head, base):
    require(run["event"] == "pull_request" and run["head_sha"] == head
            and (not run.get("pull_requests") or any(pr["number"] == number for pr in run["pull_requests"])), "CI belongs to another PR or revision.")
    require(run["conclusion"] == "success", "Release PR CI failed or was cancelled; no merge or tag.")
    matches = [job for job in jobs if job["name"] == "Windows tests and checks"]
    require(len(matches) == 1 and matches[0]["conclusion"] == "success", "Full Windows CI must succeed, not skip.")
    marker = f"Release CI identity: PR {number} head {head} base {base}"
    require(any(step["name"] == marker and step["conclusion"] == "success" for step in matches[0].get("steps", [])),
            "CI did not verify the expected release head and main base.")


def wait_ci(number, target, head, base):
    deadline = time.monotonic() + 3600
    while time.monotonic() < deadline:
        pr = api(f"pulls/{number}")
        check_pr(pr, target, head)
        require(pr["merged"] or pr["base"]["sha"] == base, "main moved; rebase the release PR and rerun CI.")
        runs = api(f"actions/workflows/ci.yml/runs?event=pull_request&head_sha={head}&per_page=100")["workflow_runs"]
        runs = [run for run in runs if run["head_sha"] == head and run["head_branch"] == "prepare-release/v" + target
                and (not run.get("pull_requests") or any(item["number"] == number for item in run["pull_requests"]))]
        if runs:
            run = max(runs, key=lambda item: item["id"])
            if run["status"] == "completed":
                jobs = api(f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100")["jobs"]
                full_ci(run, jobs, number, head, base)
                return
        print("Waiting for release PR CI...", flush=True)
        time.sleep(20)
    raise RuntimeError("Release CI wait timed out; PR retained, no merge or tag.")


def finish(target, number, head):
    require(bool(re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", target))
            and all(int(part) <= 65535 for part in target.split(".")), "Expected version X.Y.Z.")
    require(number > 0 and bool(re.fullmatch(r"[0-9a-f]{40}", head)), "Expected PR number and full head SHA.")
    pr = api(f"pulls/{number}")
    check_pr(pr, target, head)
    check_release_tag(target, pr)
    git("fetch", "origin", f"refs/pull/{number}/head")
    require(git("rev-parse", "FETCH_HEAD") == head, "Release PR changed while fetching.")
    parents = git("show", "-s", "--format=%P", head).split()
    require(len(parents) == 1, "Release PR must be rebased without merge commits.")
    base = parents[0]
    paths = set(git("diff", "--name-only", base, head).splitlines())
    require(paths <= {"Directory.Build.props", f"release-notes/v{target}.md"}
            and f"release-notes/v{target}.md" in paths, "Release PR can change only version fields and release notes.")
    before = git("show", f"{base}:Directory.Build.props")
    old = re.search(r"<Version>(\d+\.\d+\.\d+)</Version>", before)
    require(old is not None, "Missing base application version.")
    expected = before.replace(f"<Version>{old[1]}</Version>", f"<Version>{target}</Version>").replace(
        f"<Version>{old[1]}-dev</Version>", f"<Version>{target}-dev</Version>")
    require(git("show", f"{head}:Directory.Build.props") == expected, "Release PR modifies more than version fields.")
    notes = git("show", f"{head}:release-notes/v{target}.md")
    require(notes.startswith(f"# v{target}\n\n## English\n\n") and "\n\n## Русский\n\n" in notes,
            "Bilingual release notes are missing.")
    wait_ci(number, target, head, base)
    if not pr["merged"]:
        pr = api(f"pulls/{number}")
        check_pr(pr, target, head)
        check_release_tag(target, pr)
        if not pr["merged"]:
            require(api("git/ref/heads/main")["object"]["sha"] == base, "main advanced after CI; refusing merge.")
            result = api(f"pulls/{number}/merge", "PUT", sha=head, merge_method="squash")
            require(result.get("merged"), "GitHub refused the merge; branch protection was not bypassed.")
            pr = api(f"pulls/{number}")
    require(pr["merged"], "Release PR is not merged; refusing tag.")
    merged = pr["merge_commit_sha"]
    git("fetch", "origin", "main")
    require(git("show", "-s", "--format=%P", merged) == base, "Merged commit has a different base; refusing tag.")
    require(git("show", "-s", "--format=%T", merged) == git("show", "-s", "--format=%T", head),
            "Merged tree differs from the checked release head; refusing tag.")
    git("merge-base", "--is-ancestor", merged, "origin/main")
    tag = "v" + target
    if not check_release_tag(target, pr):
        api("git/refs", "POST", ref="refs/tags/" + tag, sha=merged)
    print(f"Release {tag} tagged at {merged}; the Release workflow handles publication.")


def main():
    require(bool(os.environ.get("GH_TOKEN")), "RELEASE_BOT_TOKEN is required.")
    finish(os.environ.get("RELEASE_VERSION", ""), int(os.environ.get("RELEASE_PR", "0")), os.environ.get("RELEASE_HEAD", ""))


if __name__ == "__main__":
    cli(main)
