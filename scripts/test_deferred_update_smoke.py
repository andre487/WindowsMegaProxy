import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import deferred_update_smoke
import installer_smoke


class DeferredUpdateSafetyTests(unittest.TestCase):
    def test_tray_waits_for_loaded_to_hide_the_window(self):
        windows = [(123, 456)]
        with patch.object(deferred_update_smoke, "workspace_windows", side_effect=[[], windows, windows]), \
                patch.object(deferred_update_smoke.user32, "IsWindowVisible", side_effect=[True, False]), \
                patch.object(deferred_update_smoke.time, "sleep") as sleep:
            self.assertEqual(deferred_update_smoke.wait_for_tray_client(), windows)
            self.assertEqual(sleep.call_count, 2)

    def test_visible_or_duplicate_tray_windows_still_fail(self):
        for windows, visible in [([(123, 456)], True), ([(123, 456), (789, 456)], False)]:
            with self.subTest(windows=windows), \
                    patch.object(deferred_update_smoke, "workspace_windows", return_value=windows), \
                    patch.object(deferred_update_smoke.user32, "IsWindowVisible", return_value=visible), \
                    patch.object(deferred_update_smoke.time, "sleep"):
                with self.assertRaisesRegex(AssertionError, "exactly one hidden"):
                    deferred_update_smoke.wait_for_tray_client()

    def test_refuses_a_developer_machine(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                deferred_update_smoke.check(None, None, None, None)

    def test_refuses_paths_outside_the_owned_fixture(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                patch.object(deferred_update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True):
            with self.assertRaisesRegex(RuntimeError, "paths"):
                deferred_update_smoke.check(None, None, Path("C:/unrelated"), Path("C:/unrelated"))

    def test_refuses_self_hosted_runners(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "self-hosted"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                deferred_update_smoke.check(None, None, None, None, fresh=True)

    def test_fresh_updates_refuse_existing_settings(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "data/WindowsMegaProxy"
            data.mkdir(parents=True)
            with patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(deferred_update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(deferred_update_smoke, "known_folder", side_effect=lambda kind: root / ("programs" if kind == 0x26 else "data")), \
                    patch.object(deferred_update_smoke, "service_state", return_value=None), \
                    patch.object(deferred_update_smoke.sp, "run") as run:
                with self.assertRaisesRegex(RuntimeError, "existing settings"):
                    deferred_update_smoke.check(None, None, root / "programs/WindowsMegaProxy", data, fresh=True)
                run.assert_not_called()

    def test_updates_suite_is_independent_of_installation_suite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            release = root / "artifacts/release-preview"
            release.mkdir(parents=True)
            setup = release / "preview-setup.exe"
            setup.touch()
            with patch("sys.argv", ["installer_smoke.py", "--suite", "updates"]), \
                    patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted"}), \
                    patch.object(installer_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(installer_smoke, "ROOT", root), \
                    patch.object(installer_smoke, "known_folder", side_effect=lambda kind: root / str(kind)), \
                    patch.object(installer_smoke, "service_state", return_value=None), \
                    patch.object(installer_smoke, "check_deferred_updates") as updates, \
                    patch.object(installer_smoke.sp, "run") as run:
                installer_smoke.main()
                updates.assert_called_once_with(setup, root / "artifacts/installer-checks",
                                                root / "38/WindowsMegaProxy", root / "35/WindowsMegaProxy", fresh=True)
                run.assert_not_called()
