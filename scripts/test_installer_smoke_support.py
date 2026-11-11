import os
import unittest
from unittest.mock import patch

import installer_smoke_support


class InstallerCleanupSafetyTests(unittest.TestCase):
    def test_never_uninstalls_on_developer_or_self_hosted_machines(self):
        for values in ({"GITHUB_ACTIONS": "false"}, {"GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "self-hosted"}):
            with self.subTest(values=values), patch.dict(os.environ, values), patch.object(installer_smoke_support.sp, "run") as run:
                with self.assertRaisesRegex(RuntimeError, "disposable"):
                    installer_smoke_support.uninstall("unused.log")
                run.assert_not_called()
