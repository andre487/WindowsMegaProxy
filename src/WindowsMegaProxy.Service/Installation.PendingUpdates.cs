using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WindowsMegaProxy;

public static partial class Installation
{
    private sealed record PreparedUpdate(AppUpdateState State, string Mode, string When);
    private static string UpdateDirectory(string root) => Path.Combine(root, "update");
    private static string PreparedPath(string root) => Path.Combine(UpdateDirectory(root), "pending.json");
    private static string WorkerPath(string root) => Path.Combine(UpdateDirectory(root), "worker", AppIdentity.ServiceExecutable);
    private static string UpdateTaskName(string root) => root.TrimEnd(Path.DirectorySeparatorChar).Equals(
        UserInstallation.ProtectedRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), UserInstallation.Sid), StringComparison.OrdinalIgnoreCase)
        ? UserInstallation.TaskName + ".Update" : PendingAppUpdate.TaskName;
    private static string UpdateTaskSecurity => "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)";

    private static string CurrentUpdateRoot(bool personal) => personal
        ? UserInstallation.ValidateDirectory(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), UserInstallation.Sid)
        : InstallRoot(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    private static PreparedUpdate ReadPrepared(string root)
    {
        string path = PreparedPath(root);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Invalid protected update record.");
        var prepared = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing protected update record.");
        if (prepared.State is null) throw new InvalidDataException("Missing signed update state.");
        StartupConfiguration.Validate(prepared.Mode);
        PendingAppUpdate.ValidateAction(prepared.When, prepared.Mode == "service");
        return prepared;
    }

    private static AppUpdatePackage VerifyPrepared(PreparedUpdate prepared) =>
        AppUpdateTrust.Verify(prepared.State.Manifest ?? [], prepared.State.Signature ?? [], AppUpdateTrust.PublicKey);

    private static void WriteUpdateRecord(bool personal, PendingAppUpdate? pending)
    {
        using var key = (personal ? Registry.CurrentUser : Registry.LocalMachine)
            .CreateSubKey(personal ? UserInstallation.RegistryPath : StartupConfiguration.RegistryPath);
        if (pending is null) key.DeleteValue(PendingAppUpdate.RegistryValue, false);
        else key.SetValue(PendingAppUpdate.RegistryValue, JsonSerializer.Serialize(pending));
    }

    private static dynamic UpdateTaskFolder()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        return scheduler.GetFolder(@"\");
    }

    private static void RegisterUpdateTask(string root, bool boot = true)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "Install the verified MegaProxy update before restarting its service.";
        string user = boot ? "SYSTEM" : UserInstallation.Sid;
        int logon = boot ? 5 : 3;
        task.Principal.UserId = user; task.Principal.LogonType = logon; task.Principal.RunLevel = 1;
        task.Settings.ExecutionTimeLimit = boot ? "PT30M" : "PT0S";
        task.Settings.AllowDemandStart = !boot; task.Settings.StartWhenAvailable = false;
        task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.MultipleInstances = 2;
        if (boot) { dynamic trigger = task.Triggers.Create(8); trigger.Enabled = true; }
        dynamic action = task.Actions.Create(0);
        action.Path = WorkerPath(root); action.Arguments = "--run-staged-update";
        action.WorkingDirectory = root;
        scheduler.GetFolder(@"\").RegisterTaskDefinition(UpdateTaskName(root) + (boot ? "" : ".Apply"), task, 6, user, null, logon, UpdateTaskSecurity);
    }

    private static void CopyUpdateWorker(string source, string destination, bool top = true)
    {
        CheckDirectory(source); ProtectDataDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) || name == UserInstallation.Marker) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update worker files cannot be links.");
            File.Copy(file, Path.Combine(destination, name), true);
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(directory);
            if (top && (name.Equals("dependencies", StringComparison.OrdinalIgnoreCase) || name.Equals("update", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("versions", StringComparison.OrdinalIgnoreCase) || name.Equals("launcher", StringComparison.OrdinalIgnoreCase))) continue;
            CopyUpdateWorker(directory, Path.Combine(destination, name), false);
        }
    }

    private static async Task<int> InstallUpdateAsync(string action)
    {
        if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("Update preparation requires administrator approval.");
        if (AppIdentity.IsDev) throw new InvalidOperationException("Use run.cmd to update development builds.");
        bool personal = UserInstallation.IsUser;
        string mode = StartupConfiguration.Mode;
        PendingAppUpdate.ValidateAction(action, mode == "service");
        string when = action == "restart" ? "reboot" : action;
        string root = CurrentUpdateRoot(personal);
        CheckDirectory(root);
        string staging = UpdateDirectory(root);
        ProtectDataDirectory(staging);
        using var lease = new FileStream(Path.Combine(staging, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string target = Path.Combine(staging, "setup.exe");
        string replacement = target + ".part", backup = target + ".previous";
        bool copied = false;
        bool committed = false;
        PreparedUpdate? previous = null;
        try
        {
            if (File.Exists(PreparedPath(root))) { previous = ReadPrepared(root); _ = VerifyPrepared(previous); }
        }
        catch (Exception error) when (error is IOException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            previous = null;
            UpdateLog(personal, "The pending update record is invalid; a newly verified download is required: " + error.Message);
        }
        try
        {
            File.Delete(replacement);
            AppUpdateState? state = previous?.State;
            string cache = AppUpdateTrust.CacheDirectory;
            if (File.Exists(Path.Combine(cache, "setup.exe")))
            {
                AppUpdateTrust.CheckDirectory(cache);
                string statePath = Path.Combine(cache, "state.json");
                if ((File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(statePath).Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Invalid update state.");
                var candidate = JsonSerializer.Deserialize<AppUpdateState>(await File.ReadAllTextAsync(statePath)) ?? throw new InvalidDataException("Missing update state.");
                candidate = candidate with { Manifest = candidate.DownloadedManifest ?? candidate.Manifest, Signature = candidate.DownloadedSignature ?? candidate.Signature };
                var candidatePackage = VerifyPrepared(new(candidate, mode, when));
                if (previous is null || AppUpdateTrust.ParseVersion(candidatePackage.Version) >= AppUpdateTrust.ParseVersion(VerifyPrepared(previous).Version)) state = candidate;
            }
            if (state is null) throw new InvalidDataException("Missing update state.");
            var prepared = new PreparedUpdate(state, mode, when);
            var package = VerifyPrepared(prepared);
            if (AppUpdateTrust.ParseVersion(package.Version) < AppUpdateTrust.ParseVersion(AppUpdateTrust.CurrentVersion.ToString(3)) || (previous is null && !AppUpdateTrust.IsNewer(package.Version, AppUpdateTrust.CurrentVersion)))
                throw new InvalidDataException("Updates cannot downgrade this version.");
            if (previous is null || state != previous.State)
            {
                if (previous is not null)
                {
                    try
                    {
                        string verified = await VerifiedStagedInstallerAsync(target, VerifyPrepared(previous));
                        if (verified != target) File.Move(verified, target, overwrite: true);
                    }
                    catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException)
                    { UpdateLog(personal, "Replacing an invalid staged installer with a newly verified download: " + error.Message); }
                }
                await using var input = new FileStream(Path.Combine(AppUpdateTrust.CacheDirectory, "setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                await using var output = new FileStream(replacement, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                if (input.Length != package.Size) throw new System.Security.Cryptography.CryptographicException("Invalid update installer size.");
                await input.CopyToAsync(output);
            }
            if (File.Exists(replacement))
            {
                await AppUpdateTrust.VerifyInstallerAsync(replacement, package, CancellationToken.None);
                if (File.Exists(target)) File.Replace(replacement, target, backup); else File.Move(replacement, target);
                copied = true;
            }
            if (!copied)
            {
                string verified = await VerifiedStagedInstallerAsync(target, package);
                if (verified != target) File.Move(verified, target, overwrite: true);
            }
            else await AppUpdateTrust.VerifyInstallerAsync(target, package, CancellationToken.None);
            // Keep the known working runtime after a partially completed installation.
            if (previous is null || !File.Exists(WorkerPath(root))) CopyUpdateWorker(root, Path.GetDirectoryName(WorkerPath(root))!);
            string? oldTask = personal ? null : TaskXml(UpdateTaskFolder(), PendingAppUpdate.TaskName);
            try
            {
                SettingsStore.WriteAtomic(PreparedPath(root), JsonSerializer.Serialize(prepared));
                if (mode == "service")
                {
                    RegisterUpdateTask(root);
                    await ScAsync("config", ServiceProtocol.Name, "start=", "demand");
                }
                else if (!personal) DeleteTask(UpdateTaskFolder(), PendingAppUpdate.TaskName);
                WriteUpdateRecord(personal, new(package.Version, when));
                if (when == "now") StartUpdateWorker(root);
            }
            catch
            {
                if (previous is null) { File.Delete(PreparedPath(root)); WriteUpdateRecord(personal, null); }
                else { SettingsStore.WriteAtomic(PreparedPath(root), JsonSerializer.Serialize(previous)); WriteUpdateRecord(personal, new(VerifyPrepared(previous).Version, previous.When)); }
                if (!personal)
                {
                    dynamic folder = UpdateTaskFolder();
                    if (oldTask is null) DeleteTask(folder, PendingAppUpdate.TaskName);
                    else folder.RegisterTask(PendingAppUpdate.TaskName, oldTask, 6, "SYSTEM", null, 5, UpdateTaskSecurity);
                    if (mode == "service") await ScAsync("config", ServiceProtocol.Name, "start=", previous?.Mode == "service" ? "demand" : "auto");
                }
                throw;
            }
            // The protected copy is now authoritative; keep only one downloaded update.
            committed = true;
            try { File.Delete(backup); }
            catch (IOException error) { UpdateLog(personal, "Could not remove the previous staged installer: " + error.Message); }
            try { File.Delete(Path.Combine(AppUpdateTrust.CacheDirectory, "setup.exe")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { UpdateLog(personal, "Could not remove the downloaded update cache: " + error.GetType().Name + "."); }
            UpdateLog(personal, $"Prepared application update {package.Version}; timing={when}.");
            if (action == "restart")
            {
                var restart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "/r", "/t", "0", "/d", "p:4:1", "/c", "MegaProxy application update" }) restart.ArgumentList.Add(argument);
                using var process = Process.Start(restart) ?? throw new IOException("Could not request a Windows restart.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new IOException("Windows restart failed; the update remains scheduled for the next restart.");
            }
            return 0;
        }
        catch
        {
            if (!committed && copied && File.Exists(backup)) File.Move(backup, target, overwrite: true);
            if (copied && !File.Exists(PreparedPath(root)))
                try { File.Delete(target); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally { File.Delete(replacement); }
    }

    private static void StartUpdateWorker(string root)
    {
        // Keep the updater independent of the application's sign-in task and process tree.
        try
        {
            RegisterUpdateTask(root, boot: false);
            UpdateTaskFolder().GetTask(UpdateTaskName(root) + ".Apply").Run(null);
        }
        catch { DeleteTask(UpdateTaskFolder(), UpdateTaskName(root) + ".Apply"); throw; }
    }

    private static async Task<string> VerifiedStagedInstallerAsync(string target, AppUpdatePackage package)
    {
        try { await AppUpdateTrust.VerifyInstallerAsync(target, package, CancellationToken.None); return target; }
        catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException)
        {
            // A crash may leave the previous manifest alongside the replaced installer.
            await AppUpdateTrust.VerifyInstallerAsync(target + ".previous", package, CancellationToken.None);
            return target + ".previous";
        }
    }

    private static void UpdateLog(bool personal, string message)
    {
        try { new DiagnosticLog(Path.Combine(personal ? UserInstallation.DataDirectory : MachineSettingsStore.DefaultDirectory, "logs"), initialLimit: 100).Write(message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(message); }
    }

    private static void CancelPendingUpdate(string root, bool personal)
    {
        if (!personal) DeleteTask(UpdateTaskFolder(), PendingAppUpdate.TaskName);
        DeleteTask(UpdateTaskFolder(), UpdateTaskName(root) + ".Apply");
        WriteUpdateRecord(personal, null);
        string path = PreparedPath(root);
        if (File.Exists(path)) { CheckDirectory(UpdateDirectory(root)); File.Delete(path); }
    }

    private static int ApplyPendingUpdate()
    {
        if (!StartupConfiguration.IsElevated || AppIdentity.IsDev) throw new UnauthorizedAccessException("Applying updates requires administrator approval.");
        bool personal = UserInstallation.IsUser;
        string root = CurrentUpdateRoot(personal);
        CheckDirectory(root); CheckDirectory(UpdateDirectory(root));
        var prepared = ReadPrepared(root);
        if (prepared.When is not ("launch" or "now") || StartupConfiguration.Mode == "service") throw new InvalidOperationException("This update is not scheduled for an application start.");
        _ = VerifyPrepared(prepared);
        StartUpdateWorker(root);
        return 0;
    }

    private static async Task<int> RunStagedUpdateAsync()
    {
        if (!StartupConfiguration.IsElevated || AppIdentity.IsDev) throw new UnauthorizedAccessException("Applying staged updates requires administrator approval.");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..")).TrimEnd(Path.DirectorySeparatorChar);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        bool personal = root.Equals(UserInstallation.ProtectedRoot(programFiles, UserInstallation.Sid), StringComparison.OrdinalIgnoreCase);
        if (personal) UserInstallation.ValidateDirectory(root, programFiles, UserInstallation.Sid); else InstallRoot(root, programFiles);
        if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetDirectoryName(WorkerPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The update worker must run from its protected staging directory.");
        CheckDirectory(root); CheckDirectory(UpdateDirectory(root)); CheckDirectory(Path.GetDirectoryName(WorkerPath(root))!);
        Environment.CurrentDirectory = root;
        FileStream? lease = null;
        for (int attempt = 0; lease is null; attempt++)
        {
            try { lease = new FileStream(Path.Combine(UpdateDirectory(root), "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 60) { await Task.Delay(250); }
        }
        using (lease)
        {
            string mode = personal ? "manual" : StartupConfiguration.Mode;
            int result = 1;
            bool installerVerified = false;
            string? updateVersion = null;
            try
            {
                var prepared = ReadPrepared(root);
                mode = personal ? UserInstallation.ValidateMode(prepared.Mode) : prepared.Mode;
                var package = VerifyPrepared(prepared);
                updateVersion = package.Version;
                Version installed;
                try { installed = AssemblyName.GetAssemblyName(Path.Combine(root, "WindowsMegaProxy.Runtime.dll")).Version!; }
                catch (Exception error) when (error is IOException or BadImageFormatException)
                { installed = new Version(0, 0, 0); UpdateLog(personal, "Repairing an interrupted installation from the verified staged package."); }
                if (AppUpdateTrust.ParseVersion(package.Version) >= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build)))
                {
                    string target = Path.Combine(UpdateDirectory(root), "setup.exe");
                    target = await VerifiedStagedInstallerAsync(target, package);
                    installerVerified = true;
                    WriteUpdateRecord(personal, new(package.Version, "applying"));
                    CloseClients(root, currentSession: personal);
                    var current = new MachineSettingsStore(personal ? UserInstallation.DataDirectory : null, machine: !personal).Load();
                    var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
                    foreach (string argument in new[] { Process.GetCurrentProcess().SessionId == 0 ? "/VERYSILENT" : "/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES", personal ? "/CURRENTUSER" : "/ALLUSERS",
                        "/MODE=" + prepared.Mode, "/UPDATE=1", "/STAGEDUPDATE=1", "/REOPEN=0", "/MERGETASKS=" + (AppUpdatePolicy.Enabled(current) ? "automaticupdates" : "!automaticupdates") }) start.ArgumentList.Add(argument);
                    using var setup = Process.Start(start) ?? throw new IOException("Could not start the staged installer.");
                    await setup.WaitForExitAsync();
                    if (setup.ExitCode != 0) throw new IOException("The staged installer failed with exit code " + setup.ExitCode + ". See the Windows setup log.");
                }
                result = 0;
                UpdateLog(personal, "The staged application update completed.");
            }
            catch (Exception error) { UpdateLog(personal, $"Staged application update failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}; {error.Message}"); }
            finally
            {
                if (!personal && mode == "service" && StartupConfiguration.Mode == "service")
                {
                    try
                    {
                        await ScAsync("config", ServiceProtocol.Name, "start=", "auto");
                        using var controller = new ServiceController(ServiceProtocol.Name);
                        if (controller.Status == ServiceControllerStatus.Stopped) controller.Start();
                        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                    catch
                    {
                        await ScAsync("config", ServiceProtocol.Name, "start=", "demand");
                        UpdateLog(personal, "Could not restore the service; the staged update is retained for recovery at the next restart.");
                        throw;
                    }
                }
                if (result == 0 || !installerVerified) CancelPendingUpdate(root, personal);
                else
                {
                    WriteUpdateRecord(personal, new(updateVersion!, "failed"));
                    UpdateLog(personal, "The verified installer is retained for a manual retry after installation failure.");
                }
                lease.Dispose();
                try
                {
                    if (Process.GetCurrentProcess().SessionId != 0)
                    {
                        if (personal) UserInstallation.Launch();
                        else Process.Start(new ProcessStartInfo(Path.Combine(root, AppIdentity.Executable)) { UseShellExecute = true, WorkingDirectory = root })?.Dispose();
                    }
                }
                finally { Process.Start(new ProcessStartInfo(Path.Combine(root, AppIdentity.ServiceExecutable)) { Arguments = "--cleanup-installation", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root })?.Dispose(); }
            }
            return result;
        }
    }
}
