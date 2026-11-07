using System;
using System.IO;
using WindowsMegaProxy;
using System.Linq;
using System.Diagnostics;

internal static class InstallationChecks
{
    public static void Run()
    {
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
        if (UserInstallation.ValidateMode("manual") != "manual" || UserInstallation.ValidateMode("login") != "login")
            throw new Exception("User installations must support manual and sign-in launch.");
        foreach (string invalid in new[] { "service", "", "automatic" })
        {
            try { UserInstallation.ValidateMode(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("A user installation accepted an unsupported operating mode.");
        }
        string userRoot = UserInstallation.ProtectedRoot(programFiles, sid);
        if (UserInstallation.ValidateDirectory(Path.Combine(userRoot, "versions", "test"), programFiles, sid) != userRoot)
            throw new Exception("User runtimes must use their own protected version directory.");
        foreach (string invalid in new[] { userRoot, userRoot + @"\versions", userRoot + @"\versions\test\child", @"C:\Users\Test\AppData\Local\MegaProxy\versions\test", userRoot + @"\versions\..\elsewhere", userRoot.Replace(sid, "S-1-5-21-100-200-300-1002") + @"\versions\test" })
        {
            try { UserInstallation.ValidateDirectory(invalid, programFiles, sid); }
            catch (IOException) { continue; }
            throw new Exception("A user runtime accepted an unprotected or different user's directory.");
        }
        string root = Path.Combine(programFiles, AppIdentity.Name);
        if (StartupConfiguration.RegistryPath != @"SOFTWARE\" + AppIdentity.Name) throw new Exception("Startup settings must be isolated by channel.");
        string valid = Path.Combine(root, "versions", "0.0.1-test");
        if (Installation.InstallRoot(valid.ToUpperInvariant(), programFiles) != root)
            throw new Exception("Installation must accept case-insensitive protected version paths.");
        foreach (string path in new[] { root, Path.Combine(root, "versions"), valid + @"\child", root + @"-other\versions\1", @"C:\Users\Public\versions\1", valid + @"\..\..\..\elsewhere" })
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
