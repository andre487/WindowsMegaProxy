using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WindowsMegaProxy;

public static partial class Installation
{
    public static string InstallRoot(string directory, string programFiles)
    {
        string root = Path.GetFullPath(Path.Combine(programFiles, AppIdentity.Name));
        if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The service installer must run from its protected installation directory.");
        return root;
    }

    public static int Run(string[] args)
    {
        try
        {
            string command = args[0];
            if (command is "--apply-pending-update" or "--run-staged-update")
            {
                if (args.Length != 1) throw new ArgumentException("Invalid pending update arguments.");
                return command == "--apply-pending-update" ? ApplyPendingUpdate() : RunStagedUpdateAsync().GetAwaiter().GetResult();
            }
            if (command == "--cleanup-installation")
            {
                if (args.Length != 1) throw new ArgumentException("Invalid cleanup arguments.");
                return CleanupInstallationAsync().GetAwaiter().GetResult();
            }
            if (command == "--check-dependencies")
            {
                if (args.Length != 1) throw new ArgumentException("Invalid dependency check arguments.");
                foreach (string directory in new[] { "sing-box", "sing-box-h3" })
                    foreach (string name in new[] { AppIdentity.CoreExecutable, "libcronet.dll" })
                        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "dependencies", directory, name)))
                            throw new IOException("A bundled sing-box dependency is missing. Repair the installation.");
                if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keyscan.exe")))
                    throw new IOException("Windows OpenSSH Client is required. Repair the installation.");
                using var self = Process.GetCurrentProcess();
                string runtime = self.Modules.Cast<ProcessModule>().FirstOrDefault(module => string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))?.FileName
                    ?? Environment.ProcessPath!; // Single-file hosts link CoreCLR statically.
                Console.WriteLine($"Bundled cores: {Configuration.CoreVersion}, {Configuration.Http3CoreVersion}. .NET: {Environment.Version}; runtime: {runtime}");
                return 0;
            }
            if (command == "--install-update")
            {
                if (args.Length is not (1 or 2)) throw new ArgumentException("Invalid update arguments.");
                return InstallUpdateAsync(args.Length == 1 ? "now" : args[1]).GetAwaiter().GetResult();
            }
            if (command == "--configure-user" || UserInstallation.IsUser) return RunUser(args);
            if (AppIdentity.IsDev && command == "--migrate-dev-settings")
            {
                if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("Migration requires administrator approval.");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var (_, reply) = MigrationClientAsync(deadline.Token).GetAwaiter().GetResult();
                if (!reply.CanEdit || reply.Settings is null) throw new IOException("Could not read DEV service settings.");
                SettingsStore.PrepareDirectory();
                SettingsStore.Save(reply.Settings);
                return 0;
            }
            if (command is not ("--install-service" or "--prepare-uninstall" or "--configure-mode" or "--set-mode")) throw new ArgumentException("Unknown installation command.");
            bool changingMode = command is "--configure-mode" or "--set-mode";
            bool? automaticUpdates = changingMode ? ReadAutomaticUpdates(args) : null;
            if (!changingMode && args.Length != 1) throw new ArgumentException("Invalid installation arguments.");
            string mode = changingMode ? StartupConfiguration.Validate(args[1]) : "service";
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Installation requires administrator approval.");
            string root = InstallRoot(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            CheckDirectory(root);
            CloseClients(root);
            if (command == "--prepare-uninstall") { if (!AppIdentity.IsDev) CancelPendingUpdate(root, false); ConfigureLogin("manual"); ConfigureTray("manual"); return 0; }
            if (command == "--set-mode" && !AppIdentity.IsDev)
            {
                CancelPendingUpdate(root, false);
                Process.Start(new ProcessStartInfo(Path.Combine(root, AppIdentity.ServiceExecutable)) { Arguments = "--cleanup-installation", UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            }
            ProtectDataDirectory(MachineSettingsStore.DefaultDirectory);
            bool previouslyRunning;
            using (var registration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name))
            {
                using var previousService = registration is null ? null : new ServiceController(ServiceProtocol.Name);
                previouslyRunning = previousService is not null && previousService.Status != ServiceControllerStatus.Stopped;
            }
            byte[]? legacySettings;
            try { legacySettings = MigrateSettingsAsync().GetAwaiter().GetResult(); }
            catch
            {
                if (previouslyRunning)
                {
                    using var previousService = new ServiceController(ServiceProtocol.Name);
                    if (previousService.Status == ServiceControllerStatus.Stopped) previousService.Start();
                }
                throw;
            }
            bool configuringService = false;
            Settings? previousOptions = null;
            byte[]? previousFile = File.Exists(Path.Combine(MachineSettingsStore.DefaultDirectory, "settings.json")) ? File.ReadAllBytes(Path.Combine(MachineSettingsStore.DefaultDirectory, "settings.json")) : null;
            try
            {
                previousOptions = ConfigureAutomaticUpdates(automaticUpdates, personal: false);
                ConfigureLogin(mode);
                if (mode == "service") { configuringService = true; ConfigureAsync(legacySettings ?? previousFile, previouslyRunning).GetAwaiter().GetResult(); }
                else RemoveServiceAsync().GetAwaiter().GetResult();
            }
            catch
            {
                if (previousOptions is not null) new MachineSettingsStore().Save(previousOptions);
                ConfigureLogin(StartupConfiguration.Mode);
                if (!configuringService && legacySettings is not null)
                {
                    RestoreSettings(legacySettings);
                    if (previouslyRunning) ScAsync("start", ServiceProtocol.Name).GetAwaiter().GetResult();
                }
                throw;
            }
            using (var key = Registry.LocalMachine.CreateSubKey(StartupConfiguration.RegistryPath))
            {
                key.SetValue("Mode", mode);
                key.SetValue("HelperPath", Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable));
            }
            ConfigureTray(mode);
            if (command == "--set-mode") Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable)) { UseShellExecute = true })?.Dispose();
            return 0;
        }
        catch (Exception error)
        {
            string message = DiagnosticLog.Redact(error.Message);
            Console.Error.WriteLine(message);
            if (args[0] is "--install-update" or "--apply-pending-update" or "--run-staged-update")
            {
                // A hidden modal dialog would keep installation DLLs locked indefinitely.
                try { new DiagnosticLog(Path.Combine(SettingsStore.DirectoryPath, "logs"), initialLimit: 100)
                    .Write($"Application update installation failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}; {message}"); }
                catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException) { }
            }
            if (args[0] == "--set-mode")
            {
                if (Process.GetProcessesByName(AppIdentity.Name).Length == 0 && File.Exists(Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable)))
                    Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable)) { UseShellExecute = true })?.Dispose();
                MessageBox(IntPtr.Zero, message, "MegaProxy", 0x10);
            }
            return 1;
        }
    }

    private static async Task<(ServiceClient Client, ServiceReply Reply)> MigrationClientAsync(CancellationToken token)
    {
        var client = new ServiceClient();
        try { return (client, await client.CallAsync(new("hello"), token)); }
        catch (System.TimeoutException)
        {
            var legacy = new ServiceClient(ServiceProtocol.Name + ".Control.v2");
            return (legacy, await legacy.CallAsync(new("hello"), token));
        }
    }

    private static async Task<byte[]?> MigrateSettingsAsync()
    {
        string path = Path.Combine(MachineSettingsStore.DefaultDirectory, "settings.json");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Settings cannot be a reparse point.");
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        if (envelope["scope"]?.GetValue<string>() == "machine") return null;
        using var registration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name);
        if (registration is null) throw new IOException("Start the previous service to migrate its protected settings before switching modes.");
        using var old = new ServiceController(ServiceProtocol.Name);
        if (old.Status == ServiceControllerStatus.Stopped) { old.Start(); old.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ServiceReply reply;
        while (true)
        {
            try
            {
                var migrated = await MigrationClientAsync(deadline.Token);
                var client = migrated.Client;
                reply = migrated.Reply;
                if (reply.Testing) { await Task.Delay(200, deadline.Token); continue; }
                if (reply.Status is not null)
                    await client.CallAsync(new("stop", InstanceId: reply.InstanceId, Generation: reply.Status.Generation), deadline.Token);
                reply = await client.CallAsync(new("hello"), deadline.Token);
                break;
            }
            catch (IOException) when (!deadline.IsCancellationRequested) { await Task.Delay(200, deadline.Token); }
        }
        if (!reply.CanEdit || reply.Settings is null) throw new IOException("Could not migrate the protected service configuration.");
        old.Refresh();
        if (old.Status != ServiceControllerStatus.Stopped)
        {
            if (old.Status != ServiceControllerStatus.StopPending) old.Stop();
            old.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        byte[] original = File.ReadAllBytes(path);
        new MachineSettingsStore().Save(reply.Settings);
        return original;
    }

    private static void RestoreSettings(byte[]? original)
    {
        if (original is not null) SettingsStore.WriteAtomic(Path.Combine(MachineSettingsStore.DefaultDirectory, "settings.json"), System.Text.Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
    }

    private static async Task RemoveServiceAsync()
    {
        using (var registration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name))
            if (registration is null) return;
        using (var service = new ServiceController(ServiceProtocol.Name))
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        await ScAsync("delete", ServiceProtocol.Name);
    }

    private static void ConfigureLogin(string mode)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        string taskName = AppIdentity.Name + ".Login";
        if (mode != "login")
        {
            try { folder.DeleteTask(taskName, 0); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { }
            return;
        }
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "Start MegaProxy at sign-in with administrator privileges.";
        task.Principal.UserId = sid;
        task.Principal.LogonType = 3;
        task.Principal.RunLevel = 1;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        dynamic trigger = task.Triggers.Create(9);
        trigger.UserId = sid;
        dynamic action = task.Actions.Create(0);
        action.Path = Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable);
        action.Arguments = "--tray";
        action.WorkingDirectory = AppContext.BaseDirectory;
        folder.RegisterTaskDefinition(taskName, task, 6, sid, null, 3);
    }

    private static void CheckDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Installation directories cannot be reparse points.");
        var owner = (SecurityIdentifier)directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        if (!owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            throw new UnauthorizedAccessException("The installation directory has an untrusted owner.");
    }

    private static void ProtectDataDirectory(string path)
    {
        if (!CreateDirectory(path, IntPtr.Zero))
        {
            if (Marshal.GetLastWin32Error() != 183) throw new IOException("Could not create the service data directory.");
            CheckDirectory(path);
        }
        var directory = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void CloseClients(string root, bool currentSession = false)
    {
        foreach (var process in Process.GetProcessesByName(AppIdentity.Name))
        using (process)
        {
            try
            {
                if (currentSession && process.SessionId != Process.GetCurrentProcess().SessionId ||
                    process.MainModule?.FileName?.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) != true) continue;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    if (pid == process.Id) SendMessageTimeout(window, 0x8487, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _);
                    return true;
                }, IntPtr.Zero);
                if (!process.WaitForExit(30000)) throw new IOException("Close MegaProxy in every Windows session and retry installation.");
            }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
    }

    private static async Task ConfigureAsync(byte[]? legacySettings, bool previouslyRunning)
    {
        using var registry = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name);
        string? previous = registry?.GetValue("ImagePath") as string;
        string[]? previousEnvironment = registry?.GetValue("Environment") as string[];
        bool running = previouslyRunning;
        if (previous is not null)
        {
            using var old = new ServiceController(ServiceProtocol.Name);
            if (old.Status != ServiceControllerStatus.Stopped)
            {
                if (old.Status != ServiceControllerStatus.StopPending) old.Stop();
                old.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        string executable = Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable);
        try
        {
            await ScAsync(previous is null ? "create" : "config", ServiceProtocol.Name, "binPath=", "\"" + executable + "\"", "start=", "auto", "obj=", "LocalSystem");
            using (var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name, true))
            {
                string[] environment = (previousEnvironment ?? []).Where(value => !value.StartsWith("DOTNET_BUNDLE_EXTRACT_BASE_DIR=", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (environment.Length == 0) serviceKey!.DeleteValue("Environment", false);
                else serviceKey!.SetValue("Environment", environment, RegistryValueKind.MultiString);
            }
            await ScAsync("failure", ServiceProtocol.Name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
            await ScAsync("start", ServiceProtocol.Name);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                try { await new ServiceClient().CallAsync(new("hello"), ready.Token); break; }
                catch (Exception error) when (!ready.IsCancellationRequested && error is IOException or System.TimeoutException)
                { await Task.Delay(200, ready.Token); }
            }
            using var startup = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (startup?.GetValue(ServiceProtocol.Name) is not null)
                startup.SetValue(ServiceProtocol.Name, $"\"{Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable)}\" --tray");
        }
        catch
        {
            try
            {
                using var failed = new ServiceController(ServiceProtocol.Name);
                if (failed.Status != ServiceControllerStatus.Stopped) { failed.Stop(); failed.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                RestoreSettings(legacySettings);
                if (previous is null) await ScAsync("delete", ServiceProtocol.Name);
                else
                {
                    await ScAsync("config", ServiceProtocol.Name, "binPath=", previous);
                    using (var serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name, true))
                    {
                        if (previousEnvironment is null) serviceKey!.DeleteValue("Environment", false);
                        else serviceKey!.SetValue("Environment", previousEnvironment, RegistryValueKind.MultiString);
                    }
                    if (running) await ScAsync("start", ServiceProtocol.Name);
                }
            }
            catch (Exception rollback) { Console.Error.WriteLine("Service rollback failed: " + DiagnosticLog.Redact(rollback.Message)); }
            throw;
        }
    }

    private static async Task ScAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Service Control.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new IOException("Service Control failed: " + await output + await error);
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
