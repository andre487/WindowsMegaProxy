import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import update_smoke


class UpdateSmokeSafetyTests(unittest.TestCase):
    def test_refuses_a_developer_machine(self):
        with patch("sys.argv", ["update_smoke.py", "0.0.8"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}):
            with self.assertRaisesRegex(RuntimeError, "disposable"):
                update_smoke.main()

    def test_refuses_existing_user_data_before_installing(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "WindowsMegaProxy").mkdir()
            with patch("sys.argv", ["update_smoke.py", "0.0.8"]), patch.dict(os.environ, {"GITHUB_ACTIONS": "true"}), \
                    patch.object(update_smoke.ct.windll.shell32, "IsUserAnAdmin", return_value=True), \
                    patch.object(update_smoke, "known_folder", return_value=root), patch.object(update_smoke, "download") as download:
                with self.assertRaisesRegex(RuntimeError, "existing"):
                    update_smoke.main()
                download.assert_not_called()
