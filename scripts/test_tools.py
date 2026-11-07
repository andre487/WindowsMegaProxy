import hashlib
import ctypes as ct
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from PIL import Image

from artwork import preferred_color
from common import ROOT, download, extract, app_name, artifact_root
from dependencies import asset
from install import secure_directory
from service_control import require_production_stopped, stop_service
import run as dev_runner
import stop_prod
import uninstall_dev
import install as installer


class ToolsChecks(unittest.TestCase):
    def test_console_installer_uses_mode_helper_in_version_directory(self):
        for mode, dev in ((mode, dev) for mode in ("manual", "login", "service") for dev in (False, True)):
            with self.subTest(mode=mode, dev=dev), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary).resolve()
                name = app_name(dev)
                for directory, executable in (("app", name + ".exe"), ("service", name + ".Service.exe")):
                    (root / directory).mkdir()
                    (root / directory / executable).write_bytes(b"fixture")
                (root / "Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.99").mkdir(parents=True)
                def core(destination, *_, **__):
                    destination.mkdir(parents=True, exist_ok=True)
                    (destination / "sing-box.exe").write_bytes(b"core-fixture")
                with patch("sys.argv", ["install.py", "--mode", mode] + (["--dev"] if dev else [])), patch("ctypes.windll.shell32.IsUserAnAdmin", return_value=True), \
                     patch("install.artifact_root", return_value=root), patch("install.known_folder", return_value=root / "Program Files"), \
                     patch("install.release", return_value={"windowsdesktop": {"version": "10.0.14"}}), \
                     patch("install.secure_directory", side_effect=lambda path, **_: path.mkdir(parents=True, exist_ok=True)), \
                     patch("install.install_core", side_effect=core), patch("install.run") as configure, patch("builtins.print"), \
                     patch("run.stop_workspace_app"), patch("service_control.require_production_stopped"), patch("build.main"):
                    installer.install()
                command = configure.call_args.args
                self.assertIn("versions", Path(command[0]).parts)
                self.assertEqual(Path(command[0]).name, name + ".Service.exe")
                self.assertEqual(command[1:], ("--configure-mode", mode))

    def test_dev_cleanup_matches_only_its_own_task_names(self):
        for task in (r"\WindowsMegaProxy.Dev.Login", r"\WindowsMegaProxy.Dev-S-1-5-21-1001", r"\WindowsMegaProxy.Dev"):
            self.assertTrue(uninstall_dev.is_dev_task(task))
        for task in (r"\WindowsMegaProxy.Login", r"\WindowsMegaProxy-S-1-5-21-1001", r"\Other.Dev.Login", r"\WindowsMegaProxy.Development"):
            self.assertFalse(uninstall_dev.is_dev_task(task))
            with patch("uninstall_dev.sp.run") as mutate:
                with self.assertRaisesRegex(RuntimeError, "non-DEV"):
                    uninstall_dev.remove_startup([task], [])
                mutate.assert_not_called()

    def test_dev_removal_rejects_redirected_program_files_target(self):
        with patch("ctypes.windll.shell32.IsUserAnAdmin", return_value=True), \
             patch("uninstall_dev.known_folder", return_value=Path("C:/elsewhere/../Program Files")), \
             patch("uninstall_dev.stop_workspace_app") as close, patch("uninstall_dev.stop_service") as stop:
            with self.assertRaisesRegex(RuntimeError, "Unsafe DEV"):
                uninstall_dev.main()
        close.assert_not_called()
        stop.assert_not_called()

    def test_dev_cleanup_without_registrations_keeps_running_standalone(self):
        with tempfile.TemporaryDirectory() as temporary, patch("uninstall_dev.known_folder", return_value=Path(temporary).resolve()), \
             patch("uninstall_dev.startup_records", return_value=([], [])), patch("uninstall_dev.service_state", return_value=None), \
             patch("uninstall_dev.stop_workspace_app") as close, patch("uninstall_dev.stop_service") as stop, patch("builtins.print"):
            uninstall_dev.cleanup()
            uninstall_dev.cleanup()
        close.assert_not_called()
        stop.assert_not_called()

    def test_dev_run_builds_only_ui_and_never_installs_service(self):
        with patch("run.require_production_stopped"), patch("run.configured_mode", return_value="manual"), patch("run.stop_workspace_app"), patch("run.stop_service") as stop, \
             patch("run.build.main") as build, patch("run.sp.run") as install, patch("run.sp.Popen") as launch:
            dev_runner.restart()
        stop.assert_called_once_with("WindowsMegaProxy.Dev")
        build.assert_called_once_with(dev=True, app_only=True)
        install.assert_not_called()
        self.assertIn("artifacts", launch.call_args.args[0][0])

    def test_dev_run_preserves_login_and_service_modes(self):
        for mode in ("login", "service"):
            with self.subTest(mode=mode), patch("run.require_production_stopped"), patch("run.configured_mode", return_value=mode), \
                 patch("run.sp.run") as deploy, patch("run.build.main") as build, patch("uninstall_dev.cleanup") as cleanup:
                dev_runner.restart()
            arguments = deploy.call_args.args[0]
            self.assertEqual(arguments[arguments.index("--mode") + 1], mode)
            self.assertIn("--launch", arguments)
            build.assert_not_called()
            cleanup.assert_not_called()

    def test_failed_mode_switch_reopens_the_ui_and_reports_the_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "app").mkdir()
            (root / "app/WindowsMegaProxy.Dev.exe").write_bytes(b"fixture")
            with patch("sys.argv", ["install.py", "--dev", "--launch", "--mode", "login"]), \
                 patch("install.install_main", side_effect=RuntimeError("synthetic deployment failure")), \
                 patch("install.artifact_root", return_value=root), patch("run.workspace_windows", return_value=[]), \
                 patch("install.sp.Popen") as restart, patch("ctypes.windll.user32.MessageBoxW") as report:
                with self.assertRaisesRegex(RuntimeError, "synthetic"):
                    installer.install()
            self.assertEqual(Path(restart.call_args.args[0][0]).name, "WindowsMegaProxy.Dev.exe")
            self.assertIn(".tools/install.log", report.call_args.args[1])

    def test_stop_service_waits_for_an_existing_stop(self):
        with patch("service_control.service_state", side_effect=[3, 1]), patch("service_control.sp.run") as control, \
             patch("service_control.time.sleep"):
            stop_service("WindowsMegaProxy")
        control.assert_not_called()

    def test_dev_run_refuses_active_production_before_any_changes(self):
        with patch("run.require_production_stopped", side_effect=RuntimeError("Production is running")), \
             patch("run.stop_workspace_app") as stop, patch("run.build.main") as build, patch("run.sp.run") as install:
            with self.assertRaisesRegex(RuntimeError, "Production"):
                dev_runner.restart()
        stop.assert_not_called()
        build.assert_not_called()
        install.assert_not_called()

    def test_production_guard_checks_service_and_processes(self):
        for state, processes, blocked in ((4, set(), True), (2, set(), True),
                (1, {"windowsmegaproxy.exe"}, True), (None, {"windowsmegaproxy.service.exe"}, True),
                (1, {"windowsmegaproxy.dev.exe"}, False), (None, set(), False)):
            with self.subTest(state=state, processes=processes), patch("service_control.service_state", return_value=state), \
                 patch("service_control.running_images", return_value=processes):
                if blocked:
                    with self.assertRaisesRegex(RuntimeError, "stop-prod.cmd"):
                        require_production_stopped()
                else:
                    require_production_stopped()

    def test_stop_prod_targets_only_production(self):
        with patch("ctypes.windll.shell32.IsUserAnAdmin", return_value=True), \
             patch("stop_prod.stop_workspace_app") as close, patch("stop_prod.stop_service") as stop, \
             patch("stop_prod.require_production_stopped") as verify, patch("builtins.print"):
            stop_prod.main()
        close.assert_called_once_with(dev=False, workspace_only=False)
        stop.assert_called_once_with("WindowsMegaProxy")
        verify.assert_called_once_with()

    def test_build_channels_and_icons_are_distinct(self):
        self.assertNotEqual(app_name(True), app_name(False))
        self.assertNotEqual(artifact_root(True), artifact_root(False))
        for name in ("app", "tray", "tray-light"):
            with Image.open(ROOT / f"src/WindowsMegaProxy/Assets/{name}.ico") as production, \
                 Image.open(ROOT / f"src/WindowsMegaProxy/Assets/dev-{name}.ico") as development:
                self.assertTrue({(16, 16), (32, 32), (48, 48), (256, 256)}.issubset(development.ico.sizes()))
                self.assertNotEqual(production.ico.getimage((16, 16)).tobytes(), development.ico.getimage((16, 16)).tobytes())

    def test_installer_rejects_untrusted_existing_directory_owner(self):
        api, kernel = unittest.mock.MagicMock(), unittest.mock.MagicMock()
        api.GetNamedSecurityInfoW.return_value = 0
        untrusted_sid = ct.create_unicode_buffer("S-1-5-21-100-200-300-1001")

        def owner_text(_, output):
            ct.cast(output, ct.POINTER(ct.c_wchar_p))[0] = ct.cast(untrusted_sid, ct.c_wchar_p)
            return 1

        api.ConvertSidToStringSidW.side_effect = owner_text
        with tempfile.TemporaryDirectory() as work, patch("ctypes.WinDLL", side_effect=[api, kernel]):
            with self.assertRaisesRegex(RuntimeError, "untrusted owner"):
                secure_directory(Path(work))
        api.SetFileSecurityW.assert_not_called()

    def test_installer_sets_an_exact_protected_acl(self):
        api, kernel = unittest.mock.MagicMock(), unittest.mock.MagicMock()
        api.GetNamedSecurityInfoW.return_value = 0
        trusted_sid = ct.create_unicode_buffer("S-1-5-18")

        def owner_text(_, output):
            ct.cast(output, ct.POINTER(ct.c_wchar_p))[0] = ct.cast(trusted_sid, ct.c_wchar_p)
            return 1

        api.ConvertSidToStringSidW.side_effect = owner_text
        with tempfile.TemporaryDirectory() as work, patch("ctypes.WinDLL", side_effect=[api, kernel]):
            secure_directory(Path(work), public=False)
        sddl = api.ConvertStringSecurityDescriptorToSecurityDescriptorW.call_args.args[0]
        self.assertEqual(sddl, "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")
        self.assertEqual(api.SetFileSecurityW.call_args.args[1], 0x80000005)

    def test_archive_cannot_escape_destination(self):
        with tempfile.TemporaryDirectory() as work:
            archive = Path(work) / "bad.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("../outside.txt", "bad")
            with self.assertRaises(RuntimeError):
                extract(archive, Path(work) / "target")
            self.assertFalse((Path(work) / "outside.txt").exists())

    def test_cached_download_verified_and_bad_download_not_published(self):
        with tempfile.TemporaryDirectory() as work:
            target = Path(work) / "asset.zip"
            target.write_bytes(b"verified")
            expected = hashlib.sha256(b"verified").hexdigest()
            with patch("urllib.request.urlopen", side_effect=AssertionError("Cache must avoid network")):
                self.assertEqual(download("https://example.invalid/asset", target, expected), target)
            response = unittest.mock.MagicMock()
            response.__enter__.return_value = response
            response.geturl.return_value = "https://example.invalid/asset"
            response.read.side_effect = [b"corrupted", b""]
            with patch("urllib.request.urlopen", return_value=response), self.assertRaises(RuntimeError):
                download("https://example.invalid/asset", target, "0" * 64)
            self.assertEqual(target.read_bytes(), b"verified")
            self.assertFalse(target.with_name("asset.zip.download").exists())

    def test_runtime_selects_x64_installer(self):
        files = [{"rid": "win-arm64", "url": "https://example/arm.exe"},
                 {"rid": "win-x64", "url": "https://example/x64.zip"},
                 {"rid": "win-x64", "url": "https://example/x64.exe"}]
        self.assertEqual(asset({"files": files}, ".exe"), files[2])

    def test_flag_palette_and_neutral_pixels(self):
        palette = [(255, 0, 0), (0, 0, 255)]
        self.assertEqual(preferred_color(Image.new("RGBA", (2, 2), "white"), palette), -1)
        self.assertEqual(preferred_color(Image.new("RGBA", (2, 2), "blue"), palette), 1)


if __name__ == "__main__":
    unittest.main()
