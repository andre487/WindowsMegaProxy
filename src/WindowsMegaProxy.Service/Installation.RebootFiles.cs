using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using WindowsMegaProxy;

public static partial class Installation
{
    public sealed record RebootFile(string Path, long Size, string Sha256);
    private static string RebootReceipt(string root) => Path.Combine(UpdateDirectory(root), "reboot-files.json");

    public static RebootFile[] RebootFiles(byte[] manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var files = new Dictionary<string, RebootFile>(StringComparer.OrdinalIgnoreCase);
        foreach (string component in new[] { "app", "sing-box", "sing-box-h3" })
        {
            foreach (var item in document.RootElement.GetProperty("components").GetProperty(component).GetProperty("files").EnumerateArray())
            {
                string path = item.GetProperty("path").GetString()!;
                ValidateRebootPath(path);
                if (path.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase)) continue;
                var file = new RebootFile(path, item.GetProperty("size").GetInt64(), item.GetProperty("sha256").GetString()!);
                if (file.Size < 0 || file.Size > AppUpdateTrust.MaximumInstallerSize ||
                    !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid reboot payload file.");
                if (files.TryGetValue(path, out var previous) && previous != file) throw new InvalidDataException("Conflicting reboot payload files.");
                files[path] = file;
            }
        }
        if (files.Count is 0 or > 4096 || files.Values.Sum(file => file.Size) > 2L * 1024 * 1024 * 1024 ||
            !files.ContainsKey(AppIdentity.ServiceExecutable) || !files.ContainsKey("WindowsMegaProxy.Runtime.dll"))
            throw new InvalidDataException("Incomplete reboot payload.");
        return files.Values.ToArray();
    }

    private static void ValidateRebootPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Split('/').Any(part =>
            part is "" or "." or ".." || part.EndsWith('.') ||
            !System.Text.RegularExpressions.Regex.IsMatch(part, "^[a-zA-Z0-9_.-]+$") ||
            System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ||
            path.StartsWith("update/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(UserInstallation.Marker, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe reboot payload path.");
    }

    private static string RebootFilePath(string root, string relative)
    {
        ValidateRebootPath(relative);
        string path = root;
        foreach (string part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reboot payload paths cannot contain links.");
        }
        return path;
    }

    public static void VerifyRebootFiles(string root, RebootFile[] files)
    {
        foreach (var file in files)
        {
            using var stream = File.OpenRead(RebootFilePath(root, file.Path));
            if (stream.Length != file.Size || Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256)
                throw new CryptographicException("Reboot payload verification failed: " + file.Path);
        }
    }

    public static bool HasQueuedRebootFiles(string root)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return new[] { "PendingFileRenameOperations", "PendingFileRenameOperations2" }.Any(name =>
            ContainsQueuedRebootFiles(root, key?.GetValue(name) as string[] ?? []));
    }

    public static bool ContainsQueuedRebootFiles(string root, string[] operations)
    {
        string prefix = @"\??\" + Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Session Manager can prefix paths with existence flags before the replacement flag.
        return operations.Any(path => (path.StartsWith("*1", StringComparison.Ordinal) || path.StartsWith("*2", StringComparison.Ordinal) ? path[2..] : path)
            .TrimStart('!').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireNoQueuedRebootFiles(string root)
    {
        if (HasQueuedRebootFiles(root)) throw new IOException("Restart Windows to finish the pending file replacement before changing or repairing this installation.");
    }

    // The receipt is written by the elevated verifier in an administrator-only directory.
    public static void VerifyBootUpdate()
    {
        if (AppIdentity.IsDev) return;
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string receipt = RebootReceipt(root);
        if (!File.Exists(receipt) || HasQueuedRebootFiles(root)) return;
        CheckDirectory(root); CheckDirectory(UpdateDirectory(root));
        if ((File.GetAttributes(receipt) & FileAttributes.ReparsePoint) != 0 || new FileInfo(receipt).Length > 1024 * 1024)
            throw new InvalidDataException("Invalid reboot payload receipt.");
        var files = JsonSerializer.Deserialize<RebootFile[]>(File.ReadAllText(receipt)) ?? throw new InvalidDataException("Missing reboot payload receipt.");
        if (files.Length is 0 or > 4096 || !files.Any(file => file.Path == AppIdentity.ServiceExecutable)) throw new InvalidDataException("Incomplete reboot payload receipt.");
        try { VerifyRebootFiles(root, files); }
        catch (Exception error) when (error is IOException or CryptographicException)
        {
            UpdateLog(false, "Service startup blocked by an incomplete reboot update; the recovery task will retry installation: " + error.Message);
            throw;
        }
    }

    private static int StageRebootFiles(string source)
    {
        if (!StartupConfiguration.IsElevated || AppIdentity.IsDev) throw new UnauthorizedAccessException("Reboot staging requires administrator approval.");
        string root = InstallRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetDirectoryName(WorkerPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Reboot staging requires the protected update worker.");
        CheckDirectory(root); CheckDirectory(UpdateDirectory(root));
        RequireNoQueuedRebootFiles(root);
        var prepared = ReadPrepared(root);
        if (prepared.Mode != "service" || prepared.When != "reboot") throw new InvalidDataException("Reboot staging requires a deferred service update.");
        var package = VerifyPrepared(prepared);
        var files = RebootFiles(package.Manifest);
        string staging = Path.Combine(UpdateDirectory(root), "reboot");
        ProtectDataDirectory(staging);
        foreach (var file in files)
        {
            string target = RebootFilePath(staging, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(RebootFilePath(source, file.Path), target, true);
            // MoveFileEx preserves the source ACL: installed binaries must remain readable by clients.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinUsersSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                    sid == WellKnownSidType.BuiltinUsersSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(target).SetAccessControl(security);
        }
        VerifyRebootFiles(staging, files);
        SettingsStore.WriteAtomic(RebootReceipt(root), JsonSerializer.Serialize(files));
        WriteUpdateRecord(false, new(package.Version, "reboot", FilesQueued: true));
        foreach (var file in files)
        {
            string target = RebootFilePath(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!MoveFileEx(RebootFilePath(staging, file.Path), target, 0x4 | 0x1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        UpdateLog(false, $"Verified and queued {files.Length} payload files for replacement before Windows service startup.");
        return 0;
    }

    private static async Task PrepareRebootFilesAsync(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(UpdateDirectory(root), "setup.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ALLUSERS", "/MODE=service", "/BOOTUPDATE=1", "/REOPEN=0" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not stage reboot update files.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(RebootReceipt(root)) || !HasQueuedRebootFiles(root))
            throw new IOException("Reboot update preparation failed. The recovery package is retained; see the setup log.");
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
}
