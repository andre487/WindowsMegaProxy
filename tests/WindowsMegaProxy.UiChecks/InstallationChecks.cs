using System;
using System.IO;
using WindowsMegaProxy;
using System.Linq;
using System.Diagnostics;

internal static class InstallationChecks
{
    public static void Run()
    {
        var checkLock = typeof(PendingAppUpdate).GetMethod("CheckWorkerLock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string? diagnostic = null;
        bool Check(Func<Stream> open) => (bool)checkLock.Invoke(null, [open, (Action<string>)(message => diagnostic = message)])!;
        if (Check(() => throw new UnauthorizedAccessException("Protected staging directory.")) || diagnostic is null)
            throw new Exception("An inaccessible update lock must allow client recovery and log the reason.");
        if (!Check(() => throw new IOException("Sharing violation", unchecked((int)0x80070020))))
            throw new Exception("A held installer lock must still prevent a competing client startup.");
        if (Check(() => new MemoryStream()) || Check(() => throw new FileNotFoundException()) || Check(() => throw new DirectoryNotFoundException()))
            throw new Exception("An available or missing lock must not suppress client startup.");
        foreach (string state in new[] { "now", "applying" })
        {
            var pending = new PendingAppUpdate("0.0.2", state);
            if (PendingAppUpdate.Recover(pending, new Version(0, 0, 2), false)?.When != "failed" ||
                PendingAppUpdate.Recover(pending, new Version(0, 0, 1), true)?.When != state)
                throw new Exception("A dead updater must allow recovery, including a partially installed target version; a live updater must stay protected.");
        }
        if (PendingAppUpdate.Recover(new("0.0.2", "launch"), new Version(0, 0, 2), false) is not null)
            throw new Exception("Completed next-start updates must not run again.");
        foreach (bool service in new[] { false, true })
        {
            foreach (string action in new[] { "now", service ? "restart" : "launch", service ? "reboot" : "launch" })
                if (PendingAppUpdate.ValidateAction(action, service) != action) throw new Exception("Valid update timing was rejected.");
            foreach (string invalid in service ? new[] { "launch", "", "reboot --bad" } : new[] { "reboot", "restart", "", "launch --bad" })
            {
                try { PendingAppUpdate.ValidateAction(invalid, service); throw new Exception("Invalid update timing was accepted."); }
                catch (ArgumentException) { }
            }
        }
        if (Installation.ReadAutomaticUpdates(["--configure-mode", "manual"]) is not null ||
            Installation.ReadAutomaticUpdates(["--configure-mode", "manual", "--automatic-updates", "false"]) != false ||
            Installation.ReadAutomaticUpdates(["--configure-user", "login", "--automatic-updates", "true"]) != true)
            throw new Exception("Installers must support opt-out automatic updates without resetting mode-switch preferences.");
        if (StartupConfiguration.ServiceStateLabel(System.ServiceProcess.ServiceControllerStatus.Running) != "Service: running" ||
            StartupConfiguration.ServiceStateLabel(System.ServiceProcess.ServiceControllerStatus.Stopped) != "Service: stopped" ||
            StartupConfiguration.ServiceStateLabel(System.ServiceProcess.ServiceControllerStatus.StartPending) != "Service: starting…")
            throw new Exception("Windows service state labels must be independent of tunnel state.");
        foreach (string mode in new[] { "manual", "login", "service" })
        {
            if (StartupConfiguration.Validate(mode) != mode) throw new Exception("Operating mode validation changed its value.");
            var command = StartupConfiguration.ModeCommand(@"C:\Program Files\MegaProxy test", @"C:\Development space\install.cmd", mode);
            if (!command.UseShellExecute || command.Verb != "runas" || command.WindowStyle != ProcessWindowStyle.Hidden || !command.ArgumentList.Contains(mode))
                throw new Exception("Operating mode changes require an elevated, hidden helper with explicit arguments.");
            if (AppIdentity.IsDev && (!command.FileName.EndsWith(@"scripts\.venv\Scripts\python.exe") || !command.ArgumentList.Contains("--launch") || !command.ArgumentList.Contains("--elevated")))
                throw new Exception("Source DEV switches modes directly through Python, bypasses CMD and requests restart/logging.");
            if (!AppIdentity.IsDev && !command.ArgumentList.Contains("--set-mode")) throw new Exception("Installed modes use their native restart helper.");
        }
        foreach (string invalid in new[] { "", "auto", "SERVICE", "service --bad" })
        {
            try { StartupConfiguration.Validate(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid operating mode was accepted.");
        }
        string leaseName = Path.Combine(Path.GetTempPath(), "MegaProxy.Host.Check." + Guid.NewGuid().ToString("N"));
        using (StartupConfiguration.ClaimRuntime(leaseName))
        {
            try { using var duplicate = StartupConfiguration.ClaimRuntime(leaseName); throw new Exception("Two runtime hosts acquired the same lease."); }
            catch (IOException) { }
        }
        using (StartupConfiguration.ClaimRuntime(leaseName)) { }
        File.Delete(leaseName);
        const string programFiles = @"C:\Program Files";
        const string sid = "S-1-5-21-100-200-300-1001";
        if (UserInstallation.DataDirectory == SettingsStore.DirectoryPath || UserInstallation.DataDirectory == MachineSettingsStore.DefaultDirectory)
            throw new Exception("Personal profiles must be isolated from both global and automatically migrated legacy settings.");
        if (UserInstallation.ValidateMode("manual") != "manual" || UserInstallation.ValidateMode("login") != "login")
            throw new Exception("User installations must support manual and sign-in launch.");
        foreach (string invalid in new[] { "service", "", "automatic" })
        {
            try { UserInstallation.ValidateMode(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("A user installation accepted an unsupported operating mode.");
        }
        string userRoot = UserInstallation.ProtectedRoot(programFiles, sid);
        if (UserInstallation.ValidateDirectory(userRoot, programFiles, sid) != userRoot)
            throw new Exception("User runtimes must use their own fixed protected directory.");
        foreach (string invalid in new[] { userRoot + @"\versions", userRoot + @"\versions\test\child", @"C:\Users\Test\AppData\Local\MegaProxy\versions\test", userRoot + @"\versions\..\elsewhere", userRoot.Replace(sid, "S-1-5-21-100-200-300-1002") + @"\versions\test" })
        {
            try { UserInstallation.ValidateDirectory(invalid, programFiles, sid); }
            catch (IOException) { continue; }
            throw new Exception("A user runtime accepted an unprotected or different user's directory.");
        }
        string root = Path.Combine(programFiles, AppIdentity.Name);
        if (StartupConfiguration.RegistryPath != @"SOFTWARE\" + AppIdentity.Name) throw new Exception("Startup settings must be isolated by channel.");
        string valid = root;
        if (Installation.InstallRoot(valid.ToUpperInvariant(), programFiles) != root)
            throw new Exception("Installation must accept case-insensitive fixed protected paths.");
        foreach (string path in new[] { Path.Combine(root, "versions"), valid + @"\child", root + @"-other\versions\1", @"C:\Users\Public\versions\1", valid + @"\..\..\..\elsewhere" })
        {
            try { Installation.InstallRoot(path, programFiles); }
            catch (IOException) { continue; }
            throw new Exception("Unprotected installation path was accepted: " + path);
        }
        string other = Path.Combine(programFiles, AppIdentity.IsDev ? "WindowsMegaProxy" : "WindowsMegaProxy.Dev", "versions", "test");
        try { Installation.InstallRoot(other, programFiles); }
        catch (IOException) { return; }
        throw new Exception("The installer must not cross build-channel directories.");
    }
}
