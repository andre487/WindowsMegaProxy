"""Deferred-update fixtures for installer_smoke's disposable Windows installation."""
import base64
import csv
import ctypes as ct
import hashlib
import json
import os
import shutil
import subprocess as sp
import time
import winreg
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from build import product_version, publish_options
from common import ROOT, digest, dotnet
from install import known_folder
from installer_smoke_support import uninstall
from package import merge, signed_manifest, version
from run import stop_workspace_app, user32, workspace_windows
from service_control import running_images, service_state


def wait_until(check, message, seconds=120):
    for _ in range(seconds * 4):
        result = check()
        if result:
            return result
        time.sleep(0.25)
    raise AssertionError(message)


def wait_for_tray_client():
    user32.IsWindowVisible.argtypes = [ct.c_void_p]

    def hidden_window():
        windows = workspace_windows("MegaProxy")
        return windows if len(windows) == 1 and not user32.IsWindowVisible(windows[0][0]) else None

    # HWND creation precedes WPF Loaded, which hides a --tray window.
    return wait_until(hidden_window, "The boot update did not restore exactly one hidden tray client in the logged-on session.", 30)


def apply_boot_file_moves(destination, *, damage=False):
    """Emulate Session Manager only on an owned disposable runner installation."""
    if (os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted"
            or destination != known_folder(0x26) / "WindowsMegaProxy"):
        raise RuntimeError("Boot replacement simulation requires the disposable fixture.")
    stop_workspace_app(dev=False, workspace_only=False)
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\Session Manager", 0,
                        winreg.KEY_QUERY_VALUE | winreg.KEY_SET_VALUE) as registration:
        applied = 0
        for value in ("PendingFileRenameOperations", "PendingFileRenameOperations2"):
            try:
                operations = winreg.QueryValueEx(registration, value)[0]
            except FileNotFoundError:
                continue
            assert len(operations) % 2 == 0
            remaining = []
            for source, target in zip(operations[::2], operations[1::2]):
                prefix = "\\??\\"
                source_text = source[2:] if source.startswith(("*1", "*2")) else source
                destination_text = (target[2:] if target.startswith(("*1", "*2")) else target).lstrip("!")
                if not destination_text.lower().startswith((prefix + str(destination) + "\\").lower()):
                    remaining.extend((source, target))
                    continue
                assert source_text.startswith(prefix)
                original, replacement = Path(source_text[len(prefix):]), Path(destination_text[len(prefix):])
                assert original.resolve().is_relative_to((destination / "update/reboot").resolve())
                assert replacement.resolve().is_relative_to(destination.resolve())
                if damage and replacement.name == "WindowsMegaProxy.dll":
                    original.write_bytes(b"damaged reboot payload")
                original.replace(replacement)
                applied += 1
            if remaining != operations:
                winreg.SetValueEx(registration, value, 0, winreg.REG_MULTI_SZ, remaining)
        assert applied > 0, "The updater must use Windows' early-boot replacement queue."


def check(setup, logs, destination, data, *, fresh=False):
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted" or not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Deferred update fixtures require an elevated disposable GitHub runner.")
    if destination != known_folder(0x26) / "WindowsMegaProxy" or data != known_folder(0x23) / "WindowsMegaProxy":
        raise RuntimeError("Unexpected fixture installation paths.")
    wait_until(lambda: service_state("WindowsMegaProxy") is None and not destination.exists(),
               "Deferred fixtures require the preceding installer tests to finish uninstalling.", 15)
    if fresh and data.exists():
        raise RuntimeError("Refusing to replace existing settings for fresh update fixtures.")
    if not fresh and (data / "preservation-check.txt").read_text() != "preserved":
        raise RuntimeError("Deferred fixtures require installer_smoke's own preserved test data.")
    fixture = ROOT / ".tools/deferred-update-fixture"
    started = time.time()
    fixture.mkdir(parents=True, exist_ok=True)
    source = fixture / "source"
    shutil.copytree(ROOT / "src", source / "src", ignore=shutil.ignore_patterns("bin", "obj"), dirs_exist_ok=True)
    shutil.copytree(ROOT / "assets", source / "assets", dirs_exist_ok=True)
    shutil.copy2(ROOT / "Directory.Build.props", source / "Directory.Build.props")
    key = Ed25519PrivateKey.generate()
    (source / "assets/update-public-key.txt").write_text(base64.b64encode(key.public_key().public_bytes_raw()).decode() + "\n", encoding="ascii")
    payload = fixture / "payload"
    payload.mkdir(exist_ok=True)
    for project, folder in (("WindowsMegaProxy", "app"), ("WindowsMegaProxy.Service", "service")):
        output = fixture / folder
        dotnet("publish", str(source / f"src/{project}/{project}.csproj"), "-c", "Release", "-r", "win-x64",
               *publish_options(), f"-p:MegaProxyDiagnosticsRoot={ROOT}", "-p:Version=0.0.0", "-p:IncludeSourceRevisionInInformationalVersion=false", "-o", str(output))
        assert digest(output / "WindowsMegaProxy.Diagnostics.exe", "sha256") == digest(ROOT / "artifacts/diagnostics/WindowsMegaProxy.Diagnostics.exe", "sha256")
        assert (output / "Notices/Diagnostics-NOTICES.txt").is_file(), "The update fixture must include diagnostic license notices."
        merge(output, payload)
    feed = fixture / "feed"
    feed.mkdir(exist_ok=True)
    manifest = {"schemaVersion": 1, "preview": False, "platform": "win-x64", "version": version(), "components": {"installer": {
        "file": setup.name, "version": version(), "size": setup.stat().st_size, "sha256": digest(setup, "sha256"),
        "url": f"https://github.com/andre487/WindowsMegaProxy/releases/download/v{version()}/{setup.name}"}}}
    published_manifest = json.loads((setup.parent / "update-manifest.json").read_bytes())
    for component in ("app", "sing-box", "sing-box-h3"):
        manifest["components"][component] = published_manifest["components"][component]
    signed_manifest(manifest, feed, key)
    assert manifest["signingKeyId"] == hashlib.sha256(key.public_key().public_bytes_raw()).hexdigest()
    cache = known_folder(0x1c) / "WindowsMegaProxy/Updates"
    helper = destination / "WindowsMegaProxy.Service.exe"
    simulation = "WindowsMegaProxy.Update.Simulation"
    sid = next(csv.reader(sp.check_output(["whoami.exe", "/user", "/fo", "csv", "/nh"], text=True).splitlines()))[1]
    personal_home = known_folder(0x1c) / "Programs/WindowsMegaProxy"
    personal_root = known_folder(0x26) / "WindowsMegaProxy.Users" / sid

    def command(*arguments):
        sp.run([str(helper), *arguments], check=True, timeout=180)

    def prepare(mode, index):
        sp.run([str(setup), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual", "/TASKS=",
                f"/LOG={logs / f'deferred-{index}-install.log'}"], check=True, timeout=600)
        wait_until(lambda: "windowsmegaproxy.service.exe" not in running_images(), "The installer cleanup helper has not released its runtime.", 30)
        shutil.copytree(payload, destination, dirs_exist_ok=True)
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == "0.0.0"
        command("--configure-mode", mode)
        cache.mkdir(parents=True, exist_ok=True)
        shutil.copy2(setup, cache / "setup.exe")
        (cache / "state.json").write_text(json.dumps({
            "Manifest": base64.b64encode((feed / "update-manifest.json").read_bytes()).decode(),
            "Signature": base64.b64encode((feed / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")

    def pending():
        try:
            registration = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy")
        except FileNotFoundError:
            return None
        with registration:
            try:
                return json.loads(winreg.QueryValueEx(registration, "PendingUpdate")[0])
            except FileNotFoundError:
                return None

    def service_start():
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy") as registration:
            return winreg.QueryValueEx(registration, "Start")[0]

    def cleaned():
        return not (destination / "update/worker").exists() and not (destination / "update/setup.exe").exists() and not (destination / ".setup/registration").exists()

    def verify_settings():
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--installed-settings-check", "verify")
        assert (data / "preservation-check.txt").read_text() == "preserved"
        assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}

    def simulate_boot(expected_result, *, damage=False):
        sp.run(["sc.exe", "stop", "WindowsMegaProxy"], check=True, capture_output=True)
        wait_until(lambda: service_state("WindowsMegaProxy") == 1, "The fixture service did not stop.")
        wait_until(lambda: "windowsmegaproxy.service.exe" not in running_images(), "The stopped service has not released its files.", 30)
        apply_boot_file_moves(destination, damage=damage)
        started_service = sp.run(["sc.exe", "start", "WindowsMegaProxy"], capture_output=True)
        if damage:
            wait_until(lambda: service_state("WindowsMegaProxy") == 1, "An incomplete payload must not start the service.")
        else:
            assert started_service.returncode == 0
            wait_until(lambda: service_state("WindowsMegaProxy") == 4, "The updated service must start before the recovery task.")
            assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == version()
        query_pid = "(Get-CimInstance Win32_Service -Filter \"Name='WindowsMegaProxy'\").ProcessId"
        service_pid = sp.check_output(["powershell.exe", "-NoProfile", "-Command", query_pid], text=True).strip() if not damage else None
        worker = destination / "update/worker/WindowsMegaProxy.Service.exe"
        start_time = (datetime.now() + timedelta(hours=1)).strftime("%H:%M")
        sp.run(["schtasks.exe", "/Create", "/TN", simulation, "/SC", "ONCE", "/ST", start_time,
                "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", f'"{worker}" --run-staged-update', "/F"], check=True, capture_output=True)
        sp.run(["schtasks.exe", "/Run", "/TN", simulation], check=True, capture_output=True)
        wait_until(lambda: pending() is None and service_state("WindowsMegaProxy") == 4 and cleaned(), "The boot worker did not restore the service and clean staging.", 180)
        # The simulation runs the real protected worker as SYSTEM, without rebooting the runner.
        query = "(Get-ScheduledTaskInfo -TaskName 'WindowsMegaProxy.Update.Simulation').LastTaskResult"
        wait_until(lambda: sp.check_output(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", query], text=True).strip() == str(expected_result),
                   "The SYSTEM update worker reported an unexpected result.", 30)
        assert service_start() == 2
        if service_pid is not None:
            assert sp.check_output(["powershell.exe", "-NoProfile", "-Command", query_pid], text=True).strip() == service_pid, "Burn registration must not restart the updated service."
        windows = wait_for_tray_client()
        session = ct.c_uint32()
        assert ct.windll.kernel32.ProcessIdToSessionId(windows[0][1], ct.byref(session)) and session.value > 0, "The tray client must never run in session zero."
        stop_workspace_app(dev=False, workspace_only=False)
        sp.run(["schtasks.exe", "/Delete", "/TN", simulation, "/F"], check=True, capture_output=True)

    try:
        prepare("manual", 1)
        if fresh:
            (data / "preservation-check.txt").write_text("preserved")
            dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--installed-settings-check", "seed")
        command("--install-update", "launch")
        assert pending()["When"] == "launch" and not (cache / "setup.exe").exists()
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == "0.0.0"
        with sp.Popen([str(destination / "WindowsMegaProxy.exe")]) as application:
            assert application.wait(timeout=30) == 0, "The launcher must hand off before starting the old UI or TUN."
        wait_until(lambda: product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == version() and bool(workspace_windows("MegaProxy")) and cleaned(),
                   "The next-start update did not install and reopen the application.", 180)
        assert pending() is None
        verify_settings()
        stop_workspace_app(dev=False, workspace_only=False)
        prepare("service", 2)
        command("--install-update", "reboot")
        assert pending()["When"] == "reboot" and service_state("WindowsMegaProxy") == 4 and service_start() == 2
        xml = sp.check_output(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Update", "/XML"], text=True)
        tree = ET.fromstring(xml)
        namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
        assert tree.find("t:Triggers/t:BootTrigger", namespace) is not None
        assert tree.findtext("t:Settings/t:AllowStartOnDemand", namespaces=namespace) == "false"
        assert tree.findtext("t:Principals/t:Principal/t:UserId", namespaces=namespace) in {"SYSTEM", "S-1-5-18"}
        assert Path(tree.findtext("t:Actions/t:Exec/t:Command", namespaces=namespace)).is_relative_to(destination / "update/worker")
        time.sleep(1)
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == "0.0.0", "Deferral must not apply in the current session."
        refused = sp.run([str(helper), "--configure-mode", "manual"], timeout=30)
        assert refused.returncode != 0 and service_state("WindowsMegaProxy") == 4, "Queued replacements must block mode changes without stopping the service."
        simulate_boot(0)
        verify_settings()
        command("--configure-mode", "manual")
        prepare("service", 3)
        command("--install-update", "reboot")
        simulate_boot(0, damage=True)
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == version(), "Incomplete boot replacement must be repaired from the signed installer."
        verify_settings()
        command("--configure-mode", "manual")
        prepare("manual", "replacement")
        command("--install-update", "launch")
        original_hash = digest(destination / "update/setup.exe", "sha256")
        newer = json.loads((feed / "update-manifest.json").read_bytes())
        parts = list(map(int, version().split(".")))
        parts[2] += 1
        newer_version = ".".join(map(str, parts))
        newer["version"] = newer_version
        item = newer["components"]["installer"]
        item.update(version=newer_version, file=f"WindowsMegaProxy-{newer_version}-win-x64-setup.exe")
        item["url"] = f"https://github.com/andre487/WindowsMegaProxy/releases/download/v{newer_version}/{item['file']}"
        newer_feed = fixture / "newer-feed"
        newer_feed.mkdir(exist_ok=True)
        signed_manifest(newer, newer_feed, key)
        cache.mkdir(parents=True, exist_ok=True)
        (cache / "state.json").write_text(json.dumps({
            "Manifest": base64.b64encode((newer_feed / "update-manifest.json").read_bytes()).decode(),
            "Signature": base64.b64encode((newer_feed / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")
        (cache / "setup.exe").write_bytes(b"invalid replacement")
        invalid = sp.run([str(helper), "--install-update", "launch"], timeout=180)
        assert invalid.returncode != 0 and pending()["Version"] == version()
        assert digest(destination / "update/setup.exe", "sha256") == original_hash, "A rejected replacement must retain the previously staged installer."
        shutil.copy2(setup, cache / "setup.exe")
        command("--install-update", "launch")
        assert pending()["Version"] == newer_version and not (cache / "setup.exe").exists()
        assert not (destination / "update/setup.exe.previous").exists(), "A committed replacement must release the superseded payload."
        prepare("manual", 4)
        failing_source = fixture / "FailingInstaller.cs"
        failing_setup = fixture / "failing-setup.exe"
        failing_source.write_text("class Program { static int Main() { return 1; } }", encoding="utf-8")
        compiler = Path(os.environ["SystemRoot"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        sp.run([str(compiler), "/nologo", "/target:winexe", f"/out:{failing_setup}", str(failing_source)], check=True, timeout=60)
        failed_manifest = json.loads((feed / "update-manifest.json").read_bytes())
        failed_manifest["components"]["installer"].update(size=failing_setup.stat().st_size, sha256=digest(failing_setup, "sha256"))
        failed_feed = fixture / "failed-feed"
        failed_feed.mkdir(exist_ok=True)
        signed_manifest(failed_manifest, failed_feed, key)
        shutil.copy2(failing_setup, cache / "setup.exe")
        (cache / "state.json").write_text(json.dumps({
            "Manifest": base64.b64encode((failed_feed / "update-manifest.json").read_bytes()).decode(),
            "Signature": base64.b64encode((failed_feed / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")
        command("--install-update", "launch")
        worker = destination / "update/worker/WindowsMegaProxy.Service.exe"
        failed = sp.run([str(worker), "--run-staged-update"], timeout=120)
        assert failed.returncode == 1 and pending()["When"] == "failed"
        assert worker.exists() and (destination / "update/setup.exe").exists(), "Failed installation must retain its verified recovery package."
        wait_until(lambda: bool(workspace_windows("MegaProxy")), "A failed installation must reopen a usable recovery UI.", 30)
        stop_workspace_app(dev=False, workspace_only=False)
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy", 0, winreg.KEY_SET_VALUE) as registration:
            winreg.SetValueEx(registration, "PendingUpdate", 0, winreg.REG_SZ, json.dumps({"Version": version(), "When": "applying"}))
        with sp.Popen([str(destination / "WindowsMegaProxy.exe")]) as application:
            wait_until(lambda: bool(workspace_windows("MegaProxy")), "A stale applying record must not block the client.", 30)
            assert application.poll() is None
            stop_workspace_app(dev=False, workspace_only=False)
            application.wait(timeout=30)
        wait_until(lambda: "windowsmegaproxy.service.exe" not in running_images(), "Recovery helpers did not exit.", 30)
        helper.unlink()
        (destination / "WindowsMegaProxy.Runtime.dll").unlink()
        sp.run([str(setup), "/ALLUSERS", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual", "/TASKS=",
                f"/LOG={logs / 'repair-missing-helper.log'}"], check=True, timeout=600)
        assert pending() is None and helper.exists(), "The new installer must repair missing old helper/runtime files."
        verify_settings()
        uninstall(logs / "before-personal-uninstall.log")
        sp.run([str(setup), "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual", "/TASKS=",
                f"/LOG={logs / 'deferred-personal-install.log'}"], check=True, timeout=600)
        if fresh:
            dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--user-settings-check", "seed")
        wait_until(lambda: "windowsmegaproxy.service.exe" not in running_images(), "The personal cleanup helper has not released its runtime.", 30)
        shutil.copytree(payload, personal_root, dirs_exist_ok=True)
        cache.mkdir(parents=True, exist_ok=True)
        shutil.copy2(setup, cache / "setup.exe")
        (cache / "state.json").write_text(json.dumps({
            "Manifest": base64.b64encode((feed / "update-manifest.json").read_bytes()).decode(),
            "Signature": base64.b64encode((feed / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")
        sp.run([str(personal_root / "WindowsMegaProxy.Service.exe"), "--install-update", "launch"], check=True, timeout=180)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\WindowsMegaProxy\UserInstallation") as registration:
            personal_pending = json.loads(winreg.QueryValueEx(registration, "PendingUpdate")[0])
        assert personal_pending["When"] == "launch" and personal_pending["Version"] == version(), "Personal update status must be readable from HKCU."
        assert pending() is None, "Personal scheduling must not write system update status."
        sp.run([str(personal_home / "WindowsMegaProxy.Launcher.exe")], check=True, timeout=30)
        wait_until(lambda: product_version(personal_root / "WindowsMegaProxy.exe").split("+", 1)[0] == version() and bool(workspace_windows("MegaProxy")) and
                   not (personal_root / "update/worker").exists(), "The personal scheduled launcher did not apply its next-start update.", 180)
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--user-settings-check", "verify")
        assert service_state("WindowsMegaProxy") is None and pending() is None
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\WindowsMegaProxy\UserInstallation") as registration:
            try:
                winreg.QueryValueEx(registration, "PendingUpdate")
            except FileNotFoundError:
                pass
            else:
                raise AssertionError("Successful personal installation must clear its HKCU update status.")
        assert not running_images() & {"sing-box.exe", "WindowsMegaProxy.Core.exe"}
        print("Deferred next-start installation, SYSTEM boot installation, scheduling isolation and damaged-package service recovery passed; no reboot or TUN.")
    finally:
        for label, directory in (("machine", data / "logs"), ("client", known_folder(0x1c) / "WindowsMegaProxy/logs"),
                                 ("personal", known_folder(0x1c) / "WindowsMegaProxy/Personal/logs")):
            for log in directory.glob("*.log"):
                try:
                    shutil.copy2(log, logs / f"deferred-{label}-{log.name}")
                except OSError:
                    pass
        for label, directory in (("system", Path(os.environ["SystemRoot"]) / "Temp"), ("user", known_folder(0x1c) / "Temp")):
            for log in directory.glob("Setup Log*.txt"):
                if log.stat().st_mtime >= started:
                    try:
                        shutil.copy2(log, logs / f"deferred-{label}-{log.stem}.log")
                    except OSError:
                        pass
        stop_workspace_app(dev=False, workspace_only=False)
        sp.run(["schtasks.exe", "/Delete", "/TN", simulation, "/F"], check=False, capture_output=True)
        uninstall(logs / "deferred-personal-uninstall.log", personal=True)
        uninstall(logs / "deferred-uninstall.log")
