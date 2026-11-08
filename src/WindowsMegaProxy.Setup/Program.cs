using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed record CorePackage(string Folder, string Url, string Sha256, string Directory, Dictionary<string, string> Files);
internal sealed record SetupManifest(string Version, string BuildId, string VersionDirectory, CorePackage[] Cores);
internal sealed record Snapshot(string? Helper, string Mode, byte[]? Settings, string? BuildId, string? Directory, Dictionary<string, string> LegacyRegistration);

internal static class Program
{
    private const string Registration = @"SOFTWARE\WindowsMegaProxy";
    private const string LegacyUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{1D673B75-6671-42B4-97AC-99A85041D6C3}_is1";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly string Sid = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static bool Personal;
    private static string Root = "";
    private static RegistryKey Hive => Personal ? Registry.CurrentUser : Registry.LocalMachine;
    private static string KeyPath => Personal ? Registration + @"\UserInstallation" : Registration;
    private static string SettingsFile => Personal
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMegaProxy", "Personal", "settings.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsMegaProxy", "settings.json");

    private static async Task<int> Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" })) { SelfTest(); return 0; }
        try
        {
            if (args.Length != 9 || args[0] is not ("install" or "remove") || args[1] is not ("1" or "2") ||
                args[3] is not ("preserve" or "manual" or "login" or "service") ||
                args[4] is not ("preserve" or "true" or "false") || args[5] is not ("0" or "1") ||
                args[6] is not ("0" or "1") || !int.TryParse(args[7], out int action) || args[8] is not ("0" or "1"))
                throw new ArgumentException("Invalid setup arguments.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Administrator approval is required for the protected network runtime.");
            if (!string.Equals(Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Approve installation with the same administrator account that started setup.");
            Personal = args[1] == "2";
            if (Personal && args[3] == "service") throw new ArgumentException("Windows service mode is unavailable for a personal installation.");
            Root = Path.Combine(ProgramFiles, Personal ? "WindowsMegaProxy.Users" : "WindowsMegaProxy");
            Protect(Root);
            if (Personal) { Root = Path.Combine(Root, Sid); Protect(Root); }
            string logs = Path.Combine(Root, "installer-logs");
            Protect(logs);
            // Keep errors local; no profile contents, credentials or response bodies enter installer logs.
            using var log = new StreamWriter(Path.Combine(logs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log")) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
            try
            {
                using var lease = new FileStream(Path.Combine(Root, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var manifest = ReadResource<SetupManifest>("setup.json");
                Console.WriteLine($"Setup {manifest.Version}; scope {(Personal ? "user" : "system")}; action {args[0]}.");
                if (args[0] == "install") await Install(manifest, args[3], args[4], args[5] == "1", args[6] == "1", args[8] == "1");
                else await Remove(manifest, rollback: action != 3);
                Console.WriteLine("Setup completed.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static T ReadResource<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new IOException("Missing setup resource: " + name);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new IOException("Invalid setup resource.");
    }

    private static async Task Install(SetupManifest manifest, string mode, string updates, bool ssh, bool desktop, bool launch)
    {
        using var previousKey = Hive.OpenSubKey(KeyPath);
        string? oldHelper = previousKey?.GetValue("HelperPath") as string;
        if (oldHelper is not null) ValidateHelper(oldHelper);
        string oldMode = previousKey?.GetValue("Mode") as string ?? "manual";
        if (mode == "preserve") mode = oldMode;
        ValidateMode(mode, Personal);
        // Complete all downloads and extraction before closing clients or changing the active service.
        string versions = Path.Combine(Root, "versions");
        Protect(versions);
        string directory = Path.Combine(versions, manifest.VersionDirectory + "-" + Guid.NewGuid().ToString("N"));
        Protect(directory);
        bool switched = false;
        try
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip") ?? throw new IOException("Missing application payload."))
            using (var zip = new ZipArchive(stream)) ExtractPayload(zip, directory);
            foreach (var core in manifest.Cores) await DownloadCore(core, directory);
            if (ssh && !File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keyscan.exe")))
            {
                int result = await Execute(Path.Combine(Environment.SystemDirectory, "dism.exe"), TimeSpan.FromMinutes(15),
                    "/English", "/Online", "/Add-Capability", "/CapabilityName:OpenSSH.Client~~~~0.0.1.0", "/NoRestart");
                if (result != 0) throw new IOException("OpenSSH installation did not complete. Restart Windows if required, or opt out of SSH support and retry. Exit code: " + result);
            }
            var snapshot = new Snapshot(oldHelper, oldMode, ReadSettings(), previousKey?.GetValue("SetupBuildId") as string,
                previousKey?.GetValue("SetupDirectory") as string, LegacyValues());
            string stateDirectory = Path.Combine(directory, "installer-state");
            // Machine-scope DPAPI snapshots must never inherit the runtime's Users:read ACL.
            Protect(stateDirectory, readable: false);
            string transaction = Path.Combine(stateDirectory, "rollback.json");
            File.WriteAllText(transaction, JsonSerializer.Serialize(snapshot, Json));
            string helper = Path.Combine(directory, "WindowsMegaProxy.Service.exe");
            var arguments = new List<string> { Personal ? "--configure-user" : "--configure-mode", mode };
            if (updates != "preserve") arguments.AddRange(new[] { "--automatic-updates", updates });
            await Require(helper, arguments.ToArray());
            switched = true;
            try
            {
                DeployLauncher(directory);
                CreateShortcuts(directory, desktop);
                using var key = Hive.CreateSubKey(KeyPath);
                key.SetValue("SetupBuildId", manifest.BuildId);
                key.SetValue("SetupDirectory", directory);
                using (var legacy = Hive.OpenSubKey(LegacyUninstall))
                {
                    if (legacy is not null && !string.Equals((legacy.GetValue("InstallLocation") as string)?.TrimEnd(Path.DirectorySeparatorChar),
                        Personal ? LauncherDirectory : Root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("An unrelated legacy uninstall registration was found.");
                }
                Hive.DeleteSubKeyTree(LegacyUninstall, false);
            }
            catch { await Restore(snapshot, directory); switched = false; throw; }
            Console.WriteLine("Active runtime: " + directory);
            if (launch)
            {
                try
                {
                    if (Personal) await Execute(Path.Combine(LauncherDirectory, "WindowsMegaProxy.Launcher.exe"), TimeSpan.FromSeconds(30));
                    else Process.Start(new ProcessStartInfo(Path.Combine(directory, "WindowsMegaProxy.exe")) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception error) { Console.Error.WriteLine("Installation succeeded; could not launch the interface: " + error.Message); }
            }
        }
        catch
        {
            if (!switched) DeleteVersion(directory);
            throw;
        }
    }

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

    private static Dictionary<string, string> LegacyValues()
    {
        using var legacy = Hive.OpenSubKey(LegacyUninstall);
        return legacy is null ? new() : legacy.GetValueNames().ToDictionary(name => name,
            name => JsonSerializer.Serialize(new { Kind = (int)legacy.GetValueKind(name), Value = legacy.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) }));
    }

    private static async Task Restore(Snapshot previous, string currentDirectory)
    {
        await Require(Path.Combine(currentDirectory, "WindowsMegaProxy.Service.exe"), "--remove-installation");
        if (previous.Settings is not null)
        {
            void Write() { RejectReparseChain(SettingsFile); File.WriteAllBytes(SettingsFile, previous.Settings); }
            if (Personal) Unelevated(Write); else Write();
        }
        if (previous.Helper is not null)
        {
            ValidateHelper(previous.Helper);
            await Require(previous.Helper, Personal ? "--configure-user" : "--configure-mode", previous.Mode);
            DeployLauncher(Path.GetDirectoryName(previous.Helper)!);
            CreateShortcuts(Path.GetDirectoryName(previous.Helper)!, false);
            using var registration = Hive.CreateSubKey(KeyPath);
            if (previous.BuildId is not null) registration.SetValue("SetupBuildId", previous.BuildId);
            if (previous.Directory is not null) registration.SetValue("SetupDirectory", previous.Directory);
        }
        else RemoveShortcuts();
        if (previous.LegacyRegistration.Count != 0)
        {
            using var key = Hive.CreateSubKey(LegacyUninstall);
            foreach (var item in previous.LegacyRegistration)
            {
                using var document = JsonDocument.Parse(item.Value);
                var kind = (RegistryValueKind)document.RootElement.GetProperty("Kind").GetInt32();
                var value = document.RootElement.GetProperty("Value");
                key.SetValue(item.Key, kind == RegistryValueKind.DWord ? value.GetInt32() : value.GetString()!, kind);
            }
        }
    }

    private static async Task Remove(SetupManifest manifest, bool rollback)
    {
        using var key = Hive.OpenSubKey(KeyPath);
        if (key?.GetValue("SetupBuildId") as string != manifest.BuildId) return; // Superseded bundles must not remove the active version.
        string helper = key.GetValue("HelperPath") as string ?? throw new IOException("Missing active helper.");
        ValidateHelper(helper);
        string directory = Path.GetDirectoryName(helper)!;
        if (rollback)
        {
            string path = Path.Combine(directory, "installer-state", "rollback.json");
            RejectReparseChain(path);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) ?? throw new IOException("Missing rollback state.");
            await Restore(snapshot, directory);
            DeleteVersion(directory);
            return;
        }
        await Require(helper, "--remove-installation");
        RemoveShortcuts();
        if (Personal) Unelevated(() =>
        {
            string launcher = Path.Combine(LauncherDirectory, "WindowsMegaProxy.Launcher.exe");
            File.Delete(launcher);
            if (Directory.Exists(LauncherDirectory) && !Directory.EnumerateFileSystemEntries(LauncherDirectory).Any()) Directory.Delete(LauncherDirectory);
        });
        if (Personal) Hive.DeleteSubKeyTree(KeyPath, false);
        string versions = Path.Combine(Root, "versions");
        if (Directory.Exists(versions))
            foreach (string version in Directory.GetDirectories(versions)) DeleteVersion(version);
        // Keep configuration, setup logs, shared runtimes and Wintun. They are not owned exclusively by this bundle.
    }

    private static string LauncherDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsMegaProxy");
    private static void DeployLauncher(string directory)
    {
        if (!Personal) return;
        Unelevated(() =>
        {
            Directory.CreateDirectory(LauncherDirectory);
            foreach (string file in Directory.GetFiles(Path.Combine(directory, "launcher")))
                File.Copy(file, Path.Combine(LauncherDirectory, Path.GetFileName(file)), true);
        });
    }

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

    private static void ValidateHelper(string helper)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(helper))!;
        if (Path.GetFileName(helper) != "WindowsMegaProxy.Service.exe" ||
            !string.Equals(Path.GetDirectoryName(directory), Path.Combine(Root, "versions"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The helper must belong to this protected installation.");
        RejectReparseChain(helper);
        CheckProtected(directory);
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
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(Administrators) && !rule.IdentityReference.Equals(SystemAccount) &&
                (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0)
                throw new UnauthorizedAccessException("An unprivileged account can modify the installation directory.");
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
            if (!string.Equals(parent, ProgramFiles, StringComparison.OrdinalIgnoreCase)) CheckProtected(parent);
            new DirectoryInfo(path).Create(security);
        }
        CheckProtected(path);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void DeleteVersion(string directory)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.Combine(Root, "versions"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to delete outside this installation's version directory.");
        if (!Directory.Exists(directory)) return;
        CheckProtected(directory);
        void CheckTree(string path)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                RejectReparseChain(entry);
                if (Directory.Exists(entry)) CheckTree(entry);
            }
        }
        CheckTree(directory);
        Directory.Delete(directory, true);
    }

    private static string EntryPath(string root, string entry)
    {
        if (entry.Length == 0 || entry.Contains(':') || entry.Contains('\\') || entry.StartsWith('/') || entry.Split('/').Any(part => part is ".." or "." or ""))
            throw new IOException("Invalid archive entry.");
        string path = Path.GetFullPath(Path.Combine(root, entry));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Archive path escapes installation directory.");
        return path;
    }

    private static void ExtractPayload(ZipArchive archive, string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            string path = EntryPath(directory, entry.FullName);
            if (!names.Add(path) || (size += entry.Length) > 300L * 1024 * 1024) throw new IOException("Invalid application archive.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }

    private static async Task DownloadCore(CorePackage core, string directory)
    {
        var uri = new Uri(core.Url);
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/SagerNet/sing-box/releases/download/", StringComparison.Ordinal))
            throw new IOException("Unexpected core source.");
        string archivePath = Path.Combine(directory, core.Folder + ".zip");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("Insecure core redirect.");
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) != 0)
            {
                if ((total += read) > 200L * 1024 * 1024) throw new IOException("Core archive is too large.");
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        VerifyHash(archivePath, core.Sha256);
        using (var zip = ZipFile.OpenRead(archivePath))
        {
            string destination = Path.Combine(directory, "dependencies", core.Folder);
            Directory.CreateDirectory(destination);
            foreach (var file in core.Files)
            {
                string path = EntryPath(destination, file.Key);
                var entry = zip.GetEntry(core.Directory + "/" + file.Key) ?? throw new IOException("Incomplete core archive.");
                if (entry.Length > 200L * 1024 * 1024) throw new IOException("Core file is too large.");
                entry.ExtractToFile(path);
                VerifyHash(path, file.Value);
            }
        }
        File.Delete(archivePath);
        Console.WriteLine("Verified official " + core.Folder + " package.");
    }

    private static void VerifyHash(string path, string expected)
    {
        using var input = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), expected, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Installer dependency checksum mismatch.");
    }

    private static async Task Require(string executable, params string[] arguments)
    {
        int code = await Execute(executable, TimeSpan.FromMinutes(3), arguments);
        if (code != 0) throw new IOException("Installation helper failed with exit code " + code + ".");
    }

    private static async Task<int> Execute(string executable, TimeSpan timeout, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start installation helper.");
        Task output = Pump(process.StandardOutput), error = Pump(process.StandardError);
        using var deadline = new System.Threading.CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); throw new IOException("Installation helper timed out."); }
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

    private static void SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "MegaProxy.Setup.Check");
        foreach (string invalid in new[] { "../outside", "C:/outside", "/absolute", "a\\b", "a/../b", "a//b", "./a", "" })
        {
            try { EntryPath(root, invalid); }
            catch (IOException) { continue; }
            throw new Exception("Unsafe archive path accepted: " + invalid);
        }
        if (EntryPath(root, "folder/app.dll") != Path.Combine(root, "folder", "app.dll")) throw new Exception("Valid archive path rejected.");
        ValidateMode("manual", true);
        ValidateMode("login", true);
        ValidateMode("service", false);
        try { ValidateMode("service", true); throw new Exception("Personal service mode accepted."); } catch (ArgumentException) { }
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { zip.CreateEntry("../escape"); }
        stream.Position = 0;
        using (var zip = new ZipArchive(stream))
        {
            try { ExtractPayload(zip, root); throw new Exception("Zip traversal accepted."); } catch (IOException) { }
        }
        Console.WriteLine("Setup archive traversal and operating-mode checks passed; no installation performed.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct LinkedToken { public IntPtr Token; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out LinkedToken information, int length, out int returned);
}
