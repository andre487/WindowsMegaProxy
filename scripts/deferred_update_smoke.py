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
from package import merge, signed_manifest, version
from run import stop_workspace_app, workspace_windows
from service_control import running_images, service_state


def wait_until(check, message, seconds=120):
    for _ in range(seconds * 4):
        if check():
            return
        time.sleep(0.25)
    raise AssertionError(message)


def check(setup, logs, destination, data):
    if os.environ.get("GITHUB_ACTIONS") != "true" or not ct.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Deferred update fixtures require an elevated disposable GitHub runner.")
    if destination != known_folder(0x26) / "WindowsMegaProxy" or data != known_folder(0x23) / "WindowsMegaProxy":
        raise RuntimeError("Unexpected fixture installation paths.")
    wait_until(lambda: service_state("WindowsMegaProxy") is None and not destination.exists(),
               "Deferred fixtures require the preceding installer tests to finish uninstalling.", 15)
    if (data / "preservation-check.txt").read_text() != "preserved":
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
               *publish_options(), "-p:Version=0.0.0", "-p:IncludeSourceRevisionInInformationalVersion=false", "-o", str(output))
        merge(output, payload)
    feed = fixture / "feed"
    feed.mkdir(exist_ok=True)
    manifest = {"schemaVersion": 1, "preview": False, "platform": "win-x64", "version": version(), "components": {"installer": {
        "file": setup.name, "version": version(), "size": setup.stat().st_size, "sha256": digest(setup, "sha256"),
        "url": f"https://github.com/andre487/WindowsMegaProxy/releases/download/v{version()}/{setup.name}"}}}
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
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WindowsMegaProxy") as registration:
            try:
                return json.loads(winreg.QueryValueEx(registration, "PendingUpdate")[0])
            except FileNotFoundError:
                return None

    def service_start():
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy") as registration:
            return winreg.QueryValueEx(registration, "Start")[0]

    def cleaned():
        return not (destination / "update/worker").exists() and not (destination / "update/setup.exe").exists()

    def verify_settings():
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--installed-settings-check", "verify")
        assert (data / "preservation-check.txt").read_text() == "preserved"
        assert "sing-box.exe" not in running_images()

    def simulate_boot(expected_result):
        sp.run(["sc.exe", "stop", "WindowsMegaProxy"], check=True, capture_output=True)
        wait_until(lambda: service_state("WindowsMegaProxy") == 1, "The fixture service did not stop.")
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
        assert not workspace_windows("MegaProxy") and "windowsmegaproxy.exe" not in running_images(), "Boot updates must not open a session-zero UI."
        sp.run(["schtasks.exe", "/Delete", "/TN", simulation, "/F"], check=True, capture_output=True)

    try:
        prepare("manual", 1)
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
        assert pending()["When"] == "reboot" and service_state("WindowsMegaProxy") == 4 and service_start() == 3
        xml = sp.check_output(["schtasks.exe", "/Query", "/TN", "WindowsMegaProxy.Update", "/XML"], text=True)
        tree = ET.fromstring(xml)
        namespace = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}
        assert tree.find("t:Triggers/t:BootTrigger", namespace) is not None
        assert tree.findtext("t:Settings/t:AllowStartOnDemand", namespaces=namespace) == "false"
        assert tree.findtext("t:Principals/t:Principal/t:UserId", namespaces=namespace) in {"SYSTEM", "S-1-5-18"}
        assert Path(tree.findtext("t:Actions/t:Exec/t:Command", namespaces=namespace)).is_relative_to(destination / "update/worker")
        time.sleep(1)
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == "0.0.0", "Deferral must not apply in the current session."
        simulate_boot(0)
        verify_settings()
        command("--configure-mode", "manual")
        prepare("service", 3)
        command("--install-update", "reboot")
        with (destination / "update/setup.exe").open("r+b") as staged:
            staged.write(b"INVALID")
        simulate_boot(1)
        assert product_version(destination / "WindowsMegaProxy.exe").split("+", 1)[0] == "0.0.0", "A damaged staged installer must never run."
        verify_settings()
        command("--configure-mode", "manual")
        sp.run([str(setup), "/CURRENTUSER", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/MODE=manual", "/TASKS=",
                f"/LOG={logs / 'deferred-personal-install.log'}"], check=True, timeout=600)
        wait_until(lambda: "windowsmegaproxy.service.exe" not in running_images(), "The personal cleanup helper has not released its runtime.", 30)
        shutil.copytree(payload, personal_root, dirs_exist_ok=True)
        cache.mkdir(parents=True, exist_ok=True)
        shutil.copy2(setup, cache / "setup.exe")
        (cache / "state.json").write_text(json.dumps({
            "Manifest": base64.b64encode((feed / "update-manifest.json").read_bytes()).decode(),
            "Signature": base64.b64encode((feed / "update-manifest.sig").read_bytes()).decode()}), encoding="utf-8")
        sp.run([str(personal_root / "WindowsMegaProxy.Service.exe"), "--install-update", "launch"], check=True, timeout=180)
        sp.run([str(personal_home / "WindowsMegaProxy.Launcher.exe")], check=True, timeout=30)
        wait_until(lambda: product_version(personal_root / "WindowsMegaProxy.exe").split("+", 1)[0] == version() and bool(workspace_windows("MegaProxy")) and
                   not (personal_root / "update/worker").exists(), "The personal scheduled launcher did not apply its next-start update.", 180)
        dotnet("run", "--project", "tests/WindowsMegaProxy.UiChecks", "-c", "Release", "--no-build", "--", "--user-settings-check", "verify")
        assert service_state("WindowsMegaProxy") is None and pending() is None
        assert "sing-box.exe" not in running_images()
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
        if (personal_home / "unins000.exe").is_file():
            sp.run([str(personal_home / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'deferred-personal-uninstall.log'}"], check=True, timeout=120)
        if (destination / "unins000.exe").is_file():
            sp.run([str(destination / "unins000.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", f"/LOG={logs / 'deferred-uninstall.log'}"], check=True, timeout=120)
