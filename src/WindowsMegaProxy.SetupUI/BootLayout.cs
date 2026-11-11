using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

internal static class BootLayout
{
    internal static string Create()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Boot staging requires an elevated update worker.");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MegaProxy.Setup-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Setup layout already exists.");
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Setup layout cannot traverse a reparse point.");
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        return path;
    }

    internal static int Stage(string source, string expectedHash, string bundleSource)
    {
        string directory = Create();
        try
        {
            string executable = Path.Combine(directory, "WindowsMegaProxy.Setup.exe");
            File.Copy(Path.Combine(source, "WindowsMegaProxy.Setup.exe"), executable);
            using (var hash = SHA256.Create())
            using (var file = File.OpenRead(executable))
                if (!string.Equals(BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new CryptographicException("Setup engine checksum mismatch.");
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            using var process = Process.Start(new ProcessStartInfo(executable, "boot 1 " + sid + " service preserve 1 0 0 0 1 \"" + Path.Combine(source, "payload.zip") + "\" \"" + bundleSource + "\"")
                { UseShellExecute = false, CreateNoWindow = true }) ?? throw new IOException("Could not start boot staging.");
            if (!process.WaitForExit(20 * 60 * 1000)) { process.Kill(); throw new IOException("Boot staging timed out."); }
            return process.ExitCode;
        }
        finally { Directory.Delete(directory, true); }
    }
}
