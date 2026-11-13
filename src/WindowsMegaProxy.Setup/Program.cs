using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal sealed class SetupManifest
{
    public string Version { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public RuntimeFile[] Files { get; set; } = Array.Empty<RuntimeFile>();
    public RuntimePackage[] Packages { get; set; } = Array.Empty<RuntimePackage>();
    public RuntimeFile[] RuntimeFiles { get; set; } = Array.Empty<RuntimeFile>();
}
internal sealed class RuntimePackage
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
internal sealed class RuntimeFile
{
    public string Path { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Package { get; set; }
}
internal sealed class Snapshot
{
    public string Mode { get; set; } = "manual";
    public bool Existing { get; set; }
    public string? Helper { get; set; }
    public string? Inventory { get; set; }
    public bool Desktop { get; set; }
    public byte[]? Settings { get; set; }
    public string? BuildId { get; set; }
    public string[] Files { get; set; } = Array.Empty<string>();
    public string[] PreviousFiles { get; set; } = Array.Empty<string>();
    public string[] ChangedFiles { get; set; } = Array.Empty<string>();
}

internal static partial class Program
{
    private const string Registration = @"SOFTWARE\WindowsMegaProxy";
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = 64 * 1024 * 1024 };
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly string Sid = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static bool Personal;
    private static string Payload = "";
    private static string BundleSource = "";
    private static string Root = "";
    private static RegistryKey Hive => Personal ? Registry.CurrentUser : Registry.LocalMachine;
    private static string KeyPath => Personal ? Registration + @"\UserInstallation" : Registration;
    private static string SettingsFile => Personal
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMegaProxy", "Personal", "settings.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsMegaProxy", "settings.json");
    private static string Helper => Path.Combine(Root, "WindowsMegaProxy.Service.exe");
    private static string State => Path.Combine(Root, ".setup");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length is not (11 or 12) || args[0] is not ("install" or "remove" or "remove-all" or "boot" or "register") ||
                (args[0] == "boot") != (args.Length == 12) || args[1] is not ("1" or "2") ||
                args[3] is not ("preserve" or "manual" or "login" or "service") ||
                args[4] is not ("preserve" or "true" or "false") || args.Skip(5).Take(5).Any(value => value is not ("0" or "1")))
                throw new ArgumentException("Invalid setup arguments.");
            if (args[0] == "remove-all" && args[7] != "1") throw new ArgumentException("Data removal requires an explicit uninstall.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Administrator approval is required for the protected network runtime.");
            if (args[2] != Sid) throw new UnauthorizedAccessException("Approve setup with the account that started it.");
            Personal = args[1] == "2";
            Payload = Path.GetFullPath(args[10]);
            if (args[0] == "boot") BundleSource = Path.GetFullPath(args[11]);
            if (Personal && (args[3] == "service" || args[0] == "boot")) throw new ArgumentException("Personal installations cannot use service mode.");
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MegaProxy.Setup");
            Protect(logs);
            using var log = new StreamWriter(Path.Combine(logs, (Personal ? Sid : "machine") + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log")) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
            try
            {
                if (args[0] is not ("remove" or "remove-all") && InnoInstallation.Exists()) throw new IOException(InnoInstallation.Message);
                Root = Path.Combine(ProgramFiles, Personal ? "WindowsMegaProxy.Users" : "WindowsMegaProxy");
                Protect(Root);
                if (Personal) { Root = Path.Combine(Root, Sid); Protect(Root); }
                Protect(State, readable: false);
                bool removed = false;
                using (var lease = new FileStream(Path.Combine(State, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var manifest = ReadResource<SetupManifest>("setup.json");
                    if (!Guid.TryParse(manifest.BuildId, out _)) throw new IOException("Invalid setup identity.");
                    Console.WriteLine($"Setup {manifest.Version}; scope {(Personal ? "user" : "system")}; action {args[0]}.");
                    if (args[0] is "remove" or "remove-all") removed = await Remove(manifest, rollback: args[7] != "1", deleteData: args[0] == "remove-all");
                    else if (args[0] == "register") RegisterBootInstallation(manifest);
                    else await Install(manifest, args[3], args[4], args[6] == "1", args[8] == "1", args[9] == "1", args[0] == "boot");
                }
                if (removed) CleanupEmptyInstallation();
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
        using var reader = new StreamReader(stream);
        return Json.Deserialize<T>(reader.ReadToEnd());
    }

    private static async Task Install(SetupManifest manifest, string mode, string updates, bool desktop, bool launch, bool staged, bool boot)
    {
        using var previousKey = Hive.OpenSubKey(KeyPath);
        using var serviceKey = Personal ? null : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy");
        string previousMode = previousKey?.GetValue("Mode") as string ?? (serviceKey is null ? "manual" : "service");
        if (mode == "preserve") mode = previousMode;
        ValidateMode(mode, Personal);
        string directory = Path.Combine(State, "prepare-" + Guid.NewGuid().ToString("N"));
        Protect(directory, readable: false);
        try
        {
            ExtractVerifiedPayload(manifest, directory);
            await PrepareRuntime(manifest, directory);
            if (boot)
            {
                if (mode != "service") throw new IOException("Boot updates require service mode.");
                SaveBootRegistration();
                await Require(Path.Combine(Root, "update", "worker", "WindowsMegaProxy.Service.exe"), "--stage-reboot-files", directory);
                return;
            }
            string ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keyscan.exe");
            if (!File.Exists(ssh))
            {
                int result = await Execute(Path.Combine(Environment.SystemDirectory, "dism.exe"), TimeSpan.FromMinutes(15),
                    "/English", "/Online", "/Add-Capability", "/CapabilityName:OpenSSH.Client~~~~0.0.1.0", "/NoRestart");
                if (result != 0 || !File.Exists(ssh)) throw new IOException("OpenSSH Client installation failed; check Windows Update access, restart if required and retry. Exit code: " + result);
            }
            string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(directory.Length + 1).Replace('\\', '/'))
                .Where(path => Personal || !path.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase)).ToArray();
            string backup = Path.Combine(State, "backup");
            string? inventory = File.Exists(Path.Combine(State, "files.json")) ? File.ReadAllText(Path.Combine(State, "files.json")) : null;
            string[] obsoleteLauncher = ObsoleteLauncherFiles(inventory, files, Personal);
            string[] affectedFiles = files.Concat(obsoleteLauncher).ToArray();
            DeleteProtectedTree(backup);
            Protect(backup, readable: false);
            var snapshot = new Snapshot
            {
                Mode = previousMode, Existing = previousKey?.GetValue("HelperPath") is string || serviceKey is not null,
                Helper = previousKey?.GetValue("HelperPath") as string,
                Inventory = inventory,
                Desktop = File.Exists(Path.Combine(Environment.GetFolderPath(Personal ? Environment.SpecialFolder.DesktopDirectory : Environment.SpecialFolder.CommonDesktopDirectory), "MegaProxy.lnk")),
                Settings = ReadSettings(), BuildId = previousKey?.GetValue("SetupBuildId") as string,
                Files = affectedFiles, PreviousFiles = affectedFiles.Where(file => File.Exists(InstalledPath(file))).ToArray(),
                ChangedFiles = affectedFiles.Where(file => obsoleteLauncher.Contains(file) || !SameInstalledFile(directory, file)).ToArray()
            };
            if (snapshot.Helper is not null && (!Path.GetFullPath(snapshot.Helper).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(snapshot.Helper) != "WindowsMegaProxy.Service.exe")) throw new IOException("Previous helper is outside the protected installation.");
            if (!staged) await Require(Path.Combine(directory, "WindowsMegaProxy.Service.exe"), "--prepare-repair", Root);
            if (!Personal) StopService();
            bool backedUp = false, configuring = false;
            try
            {
                foreach (string file in snapshot.PreviousFiles.Where(file => snapshot.ChangedFiles.Contains(file))) BackupInstalled(backup, file);
                File.WriteAllText(Path.Combine(State, "rollback.json"), Json.Serialize(snapshot));
                backedUp = true;
                Console.WriteLine("Installing application and launcher files.");
                foreach (string file in snapshot.ChangedFiles.Except(obsoleteLauncher))
                {
                    try { WriteInstalled(directory, file); }
                    catch (Exception error) { throw new IOException("Could not install file: " + file, error); }
                }
                foreach (string file in obsoleteLauncher) DeleteInstalled(file);
                if (Personal) Unelevated(() => DeleteEmptyDirectories(LauncherDirectory));
                Console.WriteLine("Checking installed dependencies.");
                await Require(Helper, "--check-dependencies");
                configuring = true;
                Console.WriteLine("Configuring startup mode: " + mode);
                await Configure(mode, updates);
                Console.WriteLine("Creating application shortcuts.");
                CreateShortcuts(Root, desktop);
                File.WriteAllText(Path.Combine(State, "files.json"), Json.Serialize(files));
                using var key = Hive.CreateSubKey(KeyPath);
                key.SetValue("SetupBuildId", manifest.BuildId);
                key.SetValue("SetupDirectory", Root);
                using var identity = Hive.CreateSubKey(@"Software\Classes\AppUserModelId\WindowsMegaProxy");
                identity.SetValue("DisplayName", "MegaProxy");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Installation failed before rollback: " + error);
                try
                {
                    if (backedUp) await Restore(snapshot, configuring);
                    else if (snapshot.Helper is not null) await Require(snapshot.Helper, Personal ? "--configure-user" : "--configure-mode", snapshot.Mode);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Installation and rollback both failed.", error, rollbackError);
                }
                throw;
            }
            DeleteProtectedTree(Path.Combine(State, "registration"));
            StartHelper("--cleanup-installation");
            if (launch)
            {
                try
                {
                    if (Personal)
                    {
                        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                        scheduler.Connect();
                        scheduler.GetFolder(@"\").GetTask("WindowsMegaProxy.User." + Sid).Run(null);
                    }
                    else Process.Start(new ProcessStartInfo(Path.Combine(Root, "WindowsMegaProxy.exe")) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception error) { Console.Error.WriteLine("Installation succeeded; could not launch the interface: " + error.Message); }
            }
        }
        finally { DeleteProtectedTree(directory); }
    }

    private static void SaveBootRegistration()
    {
        if (!string.Equals(BundleSource, Path.Combine(Root, "update", "setup.exe"), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(BundleSource, Path.Combine(Root, "update", "setup.exe.previous"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Boot registration requires the verified installer from protected update staging.");
        RejectReparseChain(BundleSource);
        string directory = Path.Combine(State, "registration");
        Protect(directory, readable: false);
        string target = Path.Combine(directory, "setup.exe");
        File.Copy(BundleSource, target, true);
        using var hash = SHA256.Create();
        using var input = File.OpenRead(target);
        File.WriteAllText(Path.Combine(directory, "sha256.txt"), BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""));
    }

    private static void RegisterBootInstallation(SetupManifest manifest)
    {
        if (Personal) throw new IOException("Early-boot registration requires a system installation.");
        var files = VerifyInstalledInventory(manifest, Root);
        File.WriteAllText(Path.Combine(State, "files.json"), Json.Serialize(files.Select(file => file.Path).ToArray()));
        using var key = Hive.CreateSubKey(KeyPath);
        key.SetValue("SetupDirectory", Root);
        key.SetValue("SetupBuildId", manifest.BuildId);
        Console.WriteLine("Boot installation registered without restarting the service.");
    }

    internal static RuntimeFile[] VerifyInstalledInventory(SetupManifest manifest, string root)
    {
        var files = manifest.Files.Where(file => !IsLauncher(file.Path)).ToArray();
        if (files.Length == 0 || !files.Any(file => file.Path == "WindowsMegaProxy.Service.exe")) throw new IOException("Missing boot file inventory.");
        foreach (var file in files)
            if (!HashMatches(EntryPath(root, file.Path), file.Sha256)) throw new IOException("Boot file replacement is incomplete: " + file.Path);
        return files;
    }

    private static void ExtractVerifiedPayload(SetupManifest manifest, string directory)
    {
        string copy = Path.Combine(directory, "payload.zip");
        using (var input = new FileStream(Payload, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (input.Length > 512L * 1024 * 1024) throw new IOException("Setup payload is too large.");
            input.CopyTo(output);
        }
        if (!HashMatches(copy, manifest.PayloadSha256)) throw new CryptographicException("Setup payload checksum mismatch.");
        using (var zip = ZipFile.OpenRead(copy)) ExtractPayload(zip, directory);
        File.Delete(copy);
    }

    private static async Task Configure(string mode, string updates = "preserve")
    {
        var args = new List<string> { Personal ? "--configure-user" : "--configure-mode", mode };
        if (updates != "preserve") args.AddRange(new[] { "--automatic-updates", updates });
        await Require(Helper, args.ToArray());
    }

    private static async Task Restore(Snapshot snapshot, bool configuring = true)
    {
        if (configuring && File.Exists(Helper)) await Require(Helper, "--prepare-uninstall");
        if (!Personal)
        {
            StopService();
            if (!snapshot.Existing)
            {
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy");
                if (service is not null) await Require(Path.Combine(Environment.SystemDirectory, "sc.exe"), "delete", "WindowsMegaProxy");
            }
        }
        foreach (string file in snapshot.Files)
        {
            if (!snapshot.PreviousFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) DeleteInstalled(file);
            else if (snapshot.ChangedFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) WriteInstalled(Path.Combine(State, "backup"), file);
        }
        if (snapshot.Settings is not null)
        {
            void Write() { RejectReparseChain(SettingsFile); File.WriteAllBytes(SettingsFile, snapshot.Settings); }
            if (Personal) Unelevated(Write); else Write();
        }
        if (snapshot.Helper is not null)
        {
            RejectReparseChain(snapshot.Helper);
            await Require(snapshot.Helper, Personal ? "--configure-user" : "--configure-mode", snapshot.Mode);
        }
        RemoveShortcuts();
        if (snapshot.Helper is not null) CreateShortcuts(Path.GetDirectoryName(snapshot.Helper)!, snapshot.Desktop);
        if (snapshot.Inventory is null) File.Delete(Path.Combine(State, "files.json"));
        else File.WriteAllText(Path.Combine(State, "files.json"), snapshot.Inventory);
        using var key = Hive.CreateSubKey(KeyPath);
        if (!snapshot.Existing)
            foreach (string name in new[] { "Mode", "HelperPath", "SetupDirectory" }) key.DeleteValue(name, false);
        if (snapshot.BuildId is null) key.DeleteValue("SetupBuildId", false); else key.SetValue("SetupBuildId", snapshot.BuildId);
    }

    private static async Task<bool> Remove(SetupManifest manifest, bool rollback, bool deleteData)
    {
        using var key = Hive.OpenSubKey(KeyPath);
        if (key?.GetValue("SetupBuildId") as string != manifest.BuildId) return false;
        if (rollback)
        {
            // Early-boot files already belong to the running service. Registration is retried by cleanup.
            if (Directory.Exists(Path.Combine(State, "registration"))) return false;
            await Restore(Json.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(State, "rollback.json"))));
            return false;
        }
        // A fresh helper can close clients even if the installed helper or its runtime is damaged.
        string repair = Path.Combine(State, "remove-" + Guid.NewGuid().ToString("N"));
        Protect(repair, readable: false);
        try
        {
            ExtractVerifiedPayload(manifest, repair);
            await PrepareRuntime(manifest, repair);
            await Require(Path.Combine(repair, "WindowsMegaProxy.Service.exe"), "--prepare-repair", Root);
            if (!Personal) StopService();
            // The helper's uninstall path expects its fixed installation root.
            foreach (string path in Directory.GetFiles(repair, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(repair.Length + 1).Replace('\\', '/');
                if (!relative.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase)) CopyFile(repair, Root, relative);
            }
            await Require(Helper, "--prepare-uninstall");
            if (!Personal)
            {
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy");
                if (service is not null) await Require(Path.Combine(Environment.SystemDirectory, "sc.exe"), "delete", "WindowsMegaProxy");
            }
            RemoveShortcuts();
            DeleteProtectedTree(Path.Combine(Root, "update"));
            if (Personal) DeleteOwnedLauncher(repair);
            foreach (string file in Json.Deserialize<string[]>(File.ReadAllText(Path.Combine(State, "files.json")))) DeleteInstalled(file);
            File.Delete(Path.Combine(Root, "user-installation.sid"));
            if (deleteData)
            {
                Console.WriteLine("Deleting data for the selected installation scope.");
                string data = Path.GetDirectoryName(SettingsFile)!;
                if (Personal) Unelevated(() => DeleteDataTree(data));
                else if (Directory.Exists(data)) { CheckProtected(data); DeleteDataTree(data); }
                Unelevated(() =>
                {
                    string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsMegaProxy");
                    if (Personal)
                    {
                        foreach (string folder in new[] { "Updates", "logs" }) DeleteDataTree(Path.Combine(local, folder));
                    }
                    else if (Directory.Exists(local))
                    {
                        RejectReparseChain(local);
                        foreach (string folder in Directory.GetDirectories(local))
                            if (!Path.GetFileName(folder).Equals("Personal", StringComparison.OrdinalIgnoreCase)) DeleteDataTree(folder);
                        foreach (string file in Directory.GetFiles(local)) DeleteInstalledFile(file);
                        if (!Directory.EnumerateFileSystemEntries(local).Any()) Directory.Delete(local);
                    }
                });
            }
            Hive.DeleteSubKeyTree(KeyPath, false);
            Hive.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\WindowsMegaProxy", false);
            DeleteProtectedTree(Path.Combine(State, "backup"));
        }
        finally { DeleteProtectedTree(repair); }
        return true;
    }

    private static void CleanupEmptyInstallation()
    {
        File.Delete(Path.Combine(State, "install.lock"));
        File.Delete(Path.Combine(State, "rollback.json"));
        File.Delete(Path.Combine(State, "files.json"));
        DeleteEmptyDirectories(Root);
        if (Personal) Unelevated(() => DeleteEmptyDirectories(LauncherDirectory));
    }

    private static void DeleteEmptyDirectories(string path)
    {
        RejectReparseChain(path);
        if (!Directory.Exists(path)) return;
        foreach (string directory in Directory.GetDirectories(path)) DeleteEmptyDirectories(directory);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    private static void DeleteOwnedLauncher(string source)
    {
        // Read the protected repair inventory before dropping rights for user-file deletion.
        string[] files = Directory.GetFiles(Path.Combine(source, "launcher"), "*", SearchOption.AllDirectories);
        Unelevated(() =>
        {
            foreach (string file in files)
            {
                string target = Path.Combine(LauncherDirectory, file.Substring(Path.Combine(source, "launcher").Length + 1));
                DeleteInstalledFile(target);
            }
        });
    }

    internal static void DeleteDataTree(string directory)
    {
        if (!Path.IsPathRooted(directory) || Path.GetFullPath(directory) == Path.GetPathRoot(directory))
            throw new IOException("Invalid application data directory.");
        // Validate the entire tree before deletion; never follow a user-created junction.
        void Validate(string path)
        {
            RejectReparseChain(path);
            if (!Directory.Exists(path)) return;
            foreach (string entry in Directory.GetFileSystemEntries(path))
            {
                RejectReparseChain(entry);
                if (Directory.Exists(entry)) Validate(entry);
            }
        }
        Validate(directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static void StopService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WindowsMegaProxy");
        if (key is null) return;
        using var service = new ServiceController("WindowsMegaProxy");
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    private static void StartHelper(string argument)
    {
        try { Process.Start(new ProcessStartInfo(Helper, argument) { UseShellExecute = false, CreateNoWindow = true })?.Dispose(); }
        catch (Exception error) { Console.Error.WriteLine("Installation succeeded; cleanup could not start: " + error.Message); }
    }

    internal static string[] ObsoleteLauncherFiles(string? inventory, string[] files, bool personal) =>
        inventory is null || !personal ? Array.Empty<string>() :
        Json.Deserialize<string[]>(inventory).Where(IsLauncher).Except(files, StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsLauncher(string relative) => relative.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase);
    private static string InstalledPath(string relative) => IsLauncher(relative) ? EntryPath(LauncherDirectory, relative.Substring(9)) : EntryPath(Root, relative);

    private static bool SameInstalledFile(string source, string relative)
    {
        using var hash = SHA256.Create();
        using var input = File.OpenRead(EntryPath(source, relative));
        return HashMatches(InstalledPath(relative), BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""));
    }

    private static void BackupInstalled(string backup, string relative)
    {
        string source = InstalledPath(relative);
        byte[]? bytes = null;
        void Read() { RejectReparseChain(source); bytes = File.ReadAllBytes(source); }
        if (IsLauncher(relative)) Unelevated(Read); else Read();
        string target = EntryPath(backup, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes!);
    }

    private static void WriteInstalled(string source, string relative)
    {
        if (!IsLauncher(relative)) { CopyFile(source, Root, relative); return; }
        byte[] bytes = File.ReadAllBytes(EntryPath(source, relative));
        Unelevated(() =>
        {
            string target = InstalledPath(relative);
            RejectReparseChain(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        });
    }

    private static void DeleteInstalled(string relative)
    {
        void Delete() { DeleteInstalledFile(InstalledPath(relative)); }
        if (IsLauncher(relative)) Unelevated(Delete); else Delete();
    }

    internal static void DeleteInstalledFile(string path)
    {
        RejectReparseChain(path);
        try { File.Delete(path); }
        // A failed fresh installation may never have created the launcher directory.
        catch (DirectoryNotFoundException) { }
    }

    private static void CopyFile(string source, string destination, string relative)
    {
        string input = EntryPath(source, relative), output = EntryPath(destination, relative);
        RejectReparseChain(input); RejectReparseChain(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(input, output, true);
    }

    private static void DeleteProtectedTree(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(State + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetFullPath(path), Path.Combine(Root, "update"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to remove a directory outside setup state.");
        if (!Directory.Exists(path)) return;
        CheckProtected(path);
        void Check(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            { RejectReparseChain(entry); if (Directory.Exists(entry)) Check(entry); }
        }
        Check(path);
        Directory.Delete(path, true);
    }

    internal static string Quote(string value)
    {
        if (value.Contains("\"")) throw new ArgumentException("Embedded quotes are not valid setup arguments.");
        return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
    }

    internal static bool HashMatches(string path, string expected)
    {
        RejectReparseChain(path);
        if (!File.Exists(path)) return false;
        using var hash = SHA256.Create();
        using var file = File.OpenRead(path);
        return string.Equals(BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
    }
}
