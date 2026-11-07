using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WindowsMegaProxy;

public static class Installation
{
    public static string InstallRoot(string directory, string programFiles)
    {
        string root = Path.GetFullPath(Path.Combine(programFiles, AppIdentity.Name));
        string version = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string versions = Path.Combine(root, "versions") + Path.DirectorySeparatorChar;
        if (AppIdentity.IsDev || !version.StartsWith(versions, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(version), versions.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The service installer must run from a protected production version directory.");
        return root;
    }

    public static int Run(string command)
    {
        try
        {
            if (command is not ("--install-service" or "--prepare-uninstall")) throw new ArgumentException("Unknown installation command.");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Installation requires administrator approval.");
            string root = InstallRoot(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            foreach (string path in new[] { root, Path.Combine(root, "versions"), AppContext.BaseDirectory }) CheckDirectory(path);
            CloseClients();
            if (command == "--prepare-uninstall") return 0;
            ProtectDataDirectory(MachineSettingsStore.DefaultDirectory);
            ConfigureAsync().GetAwaiter().GetResult();
            // shortcut: retain prior version files for rollback until updater retention is implemented.
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(DiagnosticLog.Redact(error.Message)); return 1; }
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

    private static void CloseClients()
    {
        foreach (var process in Process.GetProcessesByName("WindowsMegaProxy"))
        using (process)
        {
            try
            {
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

    private static async Task ConfigureAsync()
    {
        using var registry = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name);
        string? previous = registry?.GetValue("ImagePath") as string;
        bool running = false;
        if (previous is not null)
        {
            using var old = new ServiceController(ServiceProtocol.Name);
            running = old.Status != ServiceControllerStatus.Stopped;
            if (running)
            {
                if (old.Status != ServiceControllerStatus.StopPending) old.Stop();
                old.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        string executable = Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable);
        try
        {
            await ScAsync(previous is null ? "create" : "config", ServiceProtocol.Name, "binPath=", "\"" + executable + "\"", "start=", "auto", "obj=", "LocalSystem");
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
                if (previous is null) await ScAsync("delete", ServiceProtocol.Name);
                else
                {
                    await ScAsync("config", ServiceProtocol.Name, "binPath=", previous);
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
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string path, IntPtr security);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
