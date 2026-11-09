import os
import unittest
from pathlib import Path
from unittest.mock import patch

import deferred_update_smoke


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
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                patch.object(deferred_update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True):
            with self.assertRaisesRegex(RuntimeError, "paths"):
                deferred_update_smoke.check(None, None, Path("C:/unrelated"), Path("C:/unrelated"))
