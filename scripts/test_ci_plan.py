import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import ci_plan


class CiPlanTests(unittest.TestCase):
    def test_conservative_selection(self):
        for paths, expected in [(['README.md', 'docs/screenshots/a.png'], 'docs'),
                                (['release-notes/v1.2.3.md'], 'docs'),
                                (['assets/strings.json', 'src/WindowsMegaProxy/MainWindow.xaml'], 'ui'),
                                (['src/WindowsMegaProxy/MainWindow.xaml.cs'], 'full'),
                                (['Directory.Build.props', 'release-notes/v1.2.3.md'], 'full'),
                                (['.github/workflows/ci.yml'], 'full'),
                                (['scripts/ci_plan.py'], 'full'),
                                (['README.md', 'src/WindowsMegaProxy.Runtime/Engine.cs'], 'full'),
                                (['unknown.file'], 'full'), ([], 'full')]:
            with self.subTest(paths=paths):
                self.assertEqual(ci_plan.mode(paths), expected)

    def setUp(self):
        self.pull = {'number': 4, 'merged_at': 'now', 'merge_commit_sha': 'main',
                     'base': {'ref': 'main'}, 'head': {'sha': 'head', 'repo': {'full_name': 'owner/repo'}}}
        self.run = {'id': 10, 'head_sha': 'head', 'event': 'pull_request', 'head_repository': {'full_name': 'owner/repo'},
                    'pull_requests': [], 'status': 'completed', 'conclusion': 'success'}
        self.job = {'name': 'Windows tests and checks', 'conclusion': 'success', 'steps': [
            {'name': 'Release CI identity: PR 4 head head base base', 'conclusion': 'success'},
            {'name': 'Verified tree: tree', 'conclusion': 'success'}]}

    def reuse(self, extra_runs=None):
        replies = [[self.pull], {'workflow_runs': [self.run] + (extra_runs or [])}, {'jobs': [self.job]}]
        with patch.object(ci_plan, 'api', side_effect=replies):
            return ci_plan.reusable('owner/repo', 'main', 'tree')

    def test_exact_tree_and_pr_identity_can_be_reused_after_merge(self):
        self.assertEqual(self.reuse(), 10)
        self.run['pull_requests'] = [{'number': 4}]
        self.assertEqual(self.reuse(), 10)

    def test_failure_or_newer_pending_run_is_not_reused(self):
        self.run['conclusion'] = 'failure'
        self.assertIsNone(self.reuse())
        self.run['conclusion'] = 'success'
        newer = copy.deepcopy(self.run)
        newer.update(id=11, status='in_progress', conclusion=None)
        self.assertIsNone(self.reuse([newer]))

    def test_wrong_tree_or_identity_and_skipped_steps_are_not_reused(self):
        for change in ('tree', 'identity', 'step', 'job'):
            with self.subTest(change=change):
                self.setUp()
                if change == 'tree':
                    self.job['steps'][1]['name'] = 'Verified tree: other'
                elif change == 'identity':
                    self.job['steps'][0]['name'] = 'Release CI identity: PR 9 head head base base'
                elif change == 'step':
                    self.job['steps'][1]['conclusion'] = 'skipped'
                else:
                    self.job['conclusion'] = 'skipped'
                self.assertIsNone(self.reuse())

    def test_wrong_pr_repository_commit_or_event_is_not_reused(self):
        for change in ('fork', 'commit', 'open', 'event', 'head', 'pr'):
            with self.subTest(change=change):
                self.setUp()
                if change == 'fork':
                    self.pull['head']['repo']['full_name'] = 'other/repo'
                elif change == 'commit':
                    self.pull['merge_commit_sha'] = 'other'
                elif change == 'open':
                    self.pull['merged_at'] = None
                elif change == 'event':
                    self.run['event'] = 'push'
                elif change == 'head':
                    self.run['head_sha'] = 'old'
                else:
                    self.run['pull_requests'] = [{'number': 9}]
                self.assertIsNone(self.reuse())


    def test_planner_falls_back_when_diff_or_github_is_unavailable(self):
        for diff, expected in [('README.md', 'docs'),
                               ('src/WindowsMegaProxy/MainWindow.xaml.cs', 'full'),
                               (ci_plan.sp.CalledProcessError(1, 'git'), 'full')]:
            with self.subTest(diff=diff), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / 'event.json').write_text(json.dumps({'before': 'base'}), encoding='utf-8')
                env = {'GITHUB_EVENT_PATH': str(root / 'event.json'), 'GITHUB_EVENT_NAME': 'push',
                       'GITHUB_REF': 'refs/heads/main', 'GITHUB_REPOSITORY': 'owner/repo',
                       'GITHUB_OUTPUT': str(root / 'output'), 'GITHUB_STEP_SUMMARY': str(root / 'summary')}
                with (
                    patch.dict(ci_plan.os.environ, env),
                    patch.object(ci_plan, 'git', side_effect=['tree', diff, 'main']),
                    patch.object(ci_plan, 'reusable', side_effect=ci_plan.sp.CalledProcessError(1, 'gh')),
                ):
                    ci_plan.main()
                self.assertEqual((root / 'output').read_text(encoding='utf-8'), f'mode={expected}\ntree=tree\n')


if __name__ == '__main__':
    unittest.main()
