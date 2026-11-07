using System;
using System.IO;
using WindowsMegaProxy;

internal static class InstallationChecks
{
    public static void Run()
    {
        const string programFiles = @"C:\Program Files";
        string root = Path.Combine(programFiles, AppIdentity.Name);
        string valid = Path.Combine(root, "versions", "0.0.1-test");
        if (!AppIdentity.IsDev && Installation.InstallRoot(valid.ToUpperInvariant(), programFiles) != root)
            throw new Exception("Installation must accept case-insensitive protected version paths.");
        foreach (string path in new[] { root, Path.Combine(root, "versions"), valid + @"\child", root + @"-other\versions\1", @"C:\Users\Public\versions\1", valid + @"\..\..\..\elsewhere" })
        {
            try { Installation.InstallRoot(path, programFiles); }
            catch (IOException) { continue; }
            throw new Exception("Unprotected installation path was accepted: " + path);
        }
        if (AppIdentity.IsDev)
        {
            try { Installation.InstallRoot(valid, programFiles); }
            catch (IOException) { return; }
            throw new Exception("DEV cannot register the production installer service.");
        }
    }
}
