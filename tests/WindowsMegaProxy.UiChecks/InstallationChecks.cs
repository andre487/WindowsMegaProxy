using System;
using System.IO;
using WindowsMegaProxy;

internal static class InstallationChecks
{
    public static void Run()
    {
        foreach (string mode in new[] { "manual", "login", "service" })
            if (StartupConfiguration.Validate(mode) != mode) throw new Exception("Operating mode validation changed its value.");
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
