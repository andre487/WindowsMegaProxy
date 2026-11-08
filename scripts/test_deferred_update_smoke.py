import os
import unittest
from pathlib import Path
from unittest.mock import patch

import deferred_update_smoke


class DeferredUpdateSafetyTests(unittest.TestCase):
    def test_refuses_a_developer_machine(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                deferred_update_smoke.check(None, None, None, None)

    def test_refuses_paths_outside_the_owned_fixture(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                patch.object(deferred_update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True):
            with self.assertRaisesRegex(RuntimeError, "paths"):
                deferred_update_smoke.check(None, None, Path("C:/unrelated"), Path("C:/unrelated"))
