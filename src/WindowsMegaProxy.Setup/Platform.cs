using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    private static byte[]? ReadSettings()
    {
        if (Personal)
        {
            byte[]? result = null;
            Unelevated(() => result = ReadSettingsFile());
            return result;
        }
        return ReadSettingsFile();
    }

    private static byte[]? ReadSettingsFile()
    {
        if (!File.Exists(SettingsFile)) return null;
        RejectReparseChain(SettingsFile);
        if (new FileInfo(SettingsFile).Length > 16 * 1024 * 1024) throw new IOException("Settings file is too large.");
        return File.ReadAllBytes(SettingsFile);
    }

    private static string LauncherDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsMegaProxy");
    private static void CreateShortcuts(string directory, bool desktop)
    {
        void Create()
        {
            string target = Personal ? Path.Combine(LauncherDirectory, "WindowsMegaProxy.Launcher.exe") : Path.Combine(directory, "WindowsMegaProxy.exe");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            foreach (string folder in new[] { Environment.GetFolderPath(Personal ? Environment.SpecialFolder.Programs : Environment.SpecialFolder.CommonPrograms),
                desktop ? Environment.GetFolderPath(Personal ? Environment.SpecialFolder.DesktopDirectory : Environment.SpecialFolder.CommonDesktopDirectory) : "" })
            {
                if (folder.Length == 0) continue;
                string path = Path.Combine(folder, "MegaProxy.lnk");
                RejectReparseChain(path);
                dynamic shortcut = shell.CreateShortcut(path);
                shortcut.TargetPath = target;
                shortcut.WorkingDirectory = Path.GetDirectoryName(target);
                shortcut.Save();
            }
        }
        if (Personal) Unelevated(Create); else Create();
    }

    private static void RemoveShortcuts()
    {
        void Remove()
        {
            foreach (var folder in new[] { Personal ? Environment.SpecialFolder.Programs : Environment.SpecialFolder.CommonPrograms,
                Personal ? Environment.SpecialFolder.DesktopDirectory : Environment.SpecialFolder.CommonDesktopDirectory })
            {
                string path = Path.Combine(Environment.GetFolderPath(folder), "MegaProxy.lnk");
                RejectReparseChain(path);
                File.Delete(path);
            }
        }
        if (Personal) Unelevated(Remove); else Remove();
    }

    private static void ValidateMode(string mode, bool personal)
    {
        if (mode is not ("manual" or "login" or "service") || personal && mode == "service") throw new ArgumentException("Invalid operating mode for this scope.");
    }

    private static void RejectReparseChain(string path)
    {
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation paths cannot contain reparse points.");
    }

    private static void CheckProtected(string path)
    {
        RejectReparseChain(path);
        var security = new DirectoryInfo(path).GetAccessControl();
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!;
        if (!owner.Equals(Administrators) && !owner.Equals(SystemAccount)) throw new UnauthorizedAccessException("Untrusted installation directory owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (UntrustedWrite(rule))
                throw new UnauthorizedAccessException("An unprivileged account can modify the installation directory.");
    }

    internal static bool UntrustedWrite(FileSystemAccessRule rule)
    {
        // Legacy Inno directories inherit these standard Program Files entries.
        if (rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null)) &&
            (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) return false;
        var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        return rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(Administrators) &&
            !rule.IdentityReference.Equals(SystemAccount) && !rule.IdentityReference.Equals(trustedInstaller) &&
            (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles | (FileSystemRights)0x50000000)) != 0;
    }

    private static void Protect(string path, bool readable = true)
    {
        RejectReparseChain(path);
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in readable ? new[] { Administrators, SystemAccount, Users } : new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, sid.Equals(Users) ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(path))
        {
            string parent = Path.GetDirectoryName(path)!;
            if (!string.Equals(parent, ProgramFiles, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parent, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)) CheckProtected(parent);
            new DirectoryInfo(path).Create(security);
        }
        CheckProtected(path);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    internal static string EntryPath(string root, string entry)
    {
        if (entry.Length == 0 || entry.Contains(":") || entry.Contains("\\") || entry.StartsWith("/", StringComparison.Ordinal) || entry.Split('/').Any(part => part is ".." or "." or ""))
            throw new IOException("Invalid archive entry.");
        foreach (string part in entry.Split('/'))
            if (!System.Text.RegularExpressions.Regex.IsMatch(part, @"^[A-Za-z0-9_.-]+$") || part.EndsWith(".", StringComparison.Ordinal) ||
                System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new IOException("Invalid Windows archive file name.");
        if (entry.StartsWith(".setup/", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("update/", StringComparison.OrdinalIgnoreCase) ||
            entry.Equals("user-installation.sid", StringComparison.OrdinalIgnoreCase)) throw new IOException("Reserved installation path.");
        string path = Path.GetFullPath(Path.Combine(root, entry));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Archive path escapes installation directory.");
        return path;
    }

    internal static void ExtractPayload(ZipArchive archive, string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            string path = EntryPath(directory, entry.FullName);
            if (!names.Add(path) || names.Count > 4096 || (size += entry.Length) > 1024L * 1024 * 1024) throw new IOException("Invalid application archive.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }

    private static async Task Require(string executable, params string[] arguments)
    {
        int code = await Execute(executable, TimeSpan.FromMinutes(3), arguments);
        if (code != 0) throw new IOException("Installation helper failed with exit code " + code + ".");
    }

    private static async Task<int> Execute(string executable, TimeSpan timeout, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = string.Join(" ", arguments.Select(Quote));
        using var process = Process.Start(start) ?? throw new IOException("Could not start installation helper.");
        Task output = Pump(process.StandardOutput), error = Pump(process.StandardError);
        if (!await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds)))
        { process.Kill(); throw new IOException("Installation helper timed out."); }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is string line) Console.WriteLine(line);
    }

    private static void Unelevated(Action action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 19, out var linked, Marshal.SizeOf<LinkedToken>(), out _))
        {
            // UAC-disabled administrator accounts have no split token (including hosted CI runners).
            if (Marshal.GetLastWin32Error() != 1312) throw new Win32Exception(Marshal.GetLastWin32Error());
            action();
            return;
        }
        using var token = new SafeAccessTokenHandle(linked.Token);
        WindowsIdentity.RunImpersonated(token, action);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LinkedToken { public IntPtr Token; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out LinkedToken information, int length, out int returned);
}
