"""Conservative CI selection and exact-tree reuse from the merged PR."""

import json
import os
import subprocess as sp
import sys
from pathlib import Path


def mode(paths):
    def docs(path):
        return path in ('README.md', 'LICENSE', 'AGENTS.md') or path.startswith(('docs/', 'release-notes/'))

    if paths and all(docs(path) for path in paths):
        return 'docs'
    if paths and all(docs(path) or path == 'assets/strings.json' or
                     path.startswith('src/WindowsMegaProxy/') and path.endswith('.xaml') for path in paths):
        return 'ui'
    return 'full'


def git(*args):
    return sp.check_output(['git', *args], text=True).strip()


def api(path):
    return json.loads(sp.check_output(['gh', 'api', path], text=True))


def reusable(repo, commit, tree):
    for pull in api(f'repos/{repo}/commits/{commit}/pulls'):
        if not (pull.get('merged_at') and pull.get('merge_commit_sha') == commit and
                pull['base']['ref'] == 'main' and pull['head']['repo'] and pull['head']['repo']['full_name'] == repo):
            continue
        head = pull['head']['sha']
        runs = api(f'repos/{repo}/actions/workflows/ci.yml/runs?event=pull_request&head_sha={head}&per_page=100')['workflow_runs']
        runs = [run for run in runs if run['head_sha'] == head and run['event'] == 'pull_request' and
                run['head_repository']['full_name'] == repo and
                (not run.get('pull_requests') or any(item['number'] == pull['number'] for item in run['pull_requests']))]
        if not runs:
            continue
        run = max(runs, key=lambda item: item['id'])
        if run['status'] != 'completed' or run['conclusion'] != 'success':
            continue
        jobs = api(f"repos/{repo}/actions/runs/{run['id']}/jobs?filter=latest&per_page=100")['jobs']
        matches = [job for job in jobs if job['name'] == 'Windows tests and checks' and job['conclusion'] == 'success']
        identity = f"Release CI identity: PR {pull['number']} head {head} base "
        if len(matches) != 1 or not any(step['name'].startswith(identity) and step['conclusion'] == 'success'
                                        for step in matches[0].get('steps', [])):
            continue
        if any(step['name'] == f'Verified tree: {tree}' and step['conclusion'] == 'success'
                                     for step in matches[0].get('steps', [])):
            return run['id']
    return None


def verify_results(needs):
    if needs['plan']['result'] != 'success':
        raise ValueError('CI plan did not succeed.')
    selected = needs['plan']['outputs']['mode']
    expected = {
        'docs': (), 'reuse': (), 'ui': ('code',), 'full': ('code', 'network', 'installer'),
    }[selected]
    for name in ('code', 'network', 'installer'):
        required = 'success' if name in expected else 'skipped'
        if needs[name]['result'] != required:
            raise ValueError(f"{name}: expected {required}, got {needs[name]['result']}.")


def main():
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    tree = git('rev-parse', 'HEAD^{tree}')
    base = event.get('pull_request', {}).get('base', {}).get('sha') or event.get('before', '')
    try:
        paths = git('diff', '--no-renames', '--name-only', base, 'HEAD').splitlines()
        selected = mode(paths)
    except sp.CalledProcessError:
        selected = 'full'
    reused = None
    if os.environ['GITHUB_EVENT_NAME'] == 'push' and os.environ['GITHUB_REF'] == 'refs/heads/main':
        try:
            reused = reusable(os.environ['GITHUB_REPOSITORY'], git('rev-parse', 'HEAD'), tree)
        except (sp.CalledProcessError, KeyError, TypeError, ValueError):
            print('PR evidence unavailable; running the selected checks.')
    if reused:
        selected = 'reuse'
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f'mode={selected}\ntree={tree}\n')
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
        summary.write(f'CI mode: **{selected}**. Git tree: `{tree}`. Reused PR run: {reused or "none"}.\n')


if __name__ == '__main__':
    if sys.argv[1:] == ['--verify']:
        verify_results(json.loads(os.environ['CI_NEEDS']))
    else:
        main()
