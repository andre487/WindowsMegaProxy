using System;
using System.IO;
using System.Threading.Tasks;
using WindowsMegaProxy;

public static partial class Installation
{
    public static bool? ReadAutomaticUpdates(string[] args)
    {
        if (args.Length == 2) return null;
        if (args.Length == 4 && args[2] == "--automatic-updates" && bool.TryParse(args[3], out bool value)) return value;
        throw new ArgumentException("Invalid automatic update option.");
    }

    private static Settings? ConfigureAutomaticUpdates(bool? enabled, bool personal)
    {
        if (enabled is null) return null;
        var store = new MachineSettingsStore(personal ? UserInstallation.DataDirectory : null, machine: !personal);
        var previous = store.Initialized ? store.Load() : personal ? new Settings() : SettingsStore.Load();
        var updated = previous.Copy();
        updated.WindowsOptions[AppUpdatePolicy.Option] = enabled.Value;
        store.Save(updated);
        return previous;
    }

    private static async Task<int> CleanupInstallationAsync()
    {
        if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("Installation cleanup requires administrator approval.");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string root = UserInstallation.IsUser ? UserInstallation.ValidateDirectory(AppContext.BaseDirectory, programFiles, UserInstallation.Sid)
            : InstallRoot(AppContext.BaseDirectory, programFiles);
        CheckDirectory(root);
        var log = new DiagnosticLog(Path.Combine(SettingsStore.DirectoryPath, "logs"), initialLimit: 100);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                string staging = Path.Combine(root, "update");
                if (Directory.Exists(staging))
                {
                    CheckDirectory(staging);
                    FileStream lease;
                    try { lease = new FileStream(Path.Combine(staging, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    // The active worker requests cleanup after it releases this lock.
                    catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { return 0; }
                    using (lease)
                    {
                        if (File.Exists(PreparedPath(root))) return 0;
                        RequireNoQueuedRebootFiles(root);
                        await CompleteBootRegistrationAsync(root, log);
                        string reboot = Path.Combine(staging, "reboot");
                        if (Directory.Exists(reboot)) { CheckDirectory(reboot); Directory.Delete(reboot, true); }
                        File.Delete(RebootReceipt(root));
                        string worker = Path.Combine(staging, "worker");
                        if (Directory.Exists(worker)) { CheckDirectory(worker); Directory.Delete(worker, true); }
                        string setup = Path.Combine(staging, "setup.exe");
                        if (File.Exists(setup)) File.Delete(setup);
                        File.Delete(setup + ".previous");
                        File.Delete(setup + ".part");
                    }
                }
                log.Write("Installation cleanup completed.");
                if (!UserInstallation.IsUser && System.Diagnostics.Process.GetCurrentProcess().SessionId == 0 && StartupConfiguration.Mode == "service")
                {
                    try { RestoreTrayClients(log); }
                    catch (Exception error) { log.Write($"Could not restore tray clients: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}."); }
                }
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt == 59) { log.Write($"Installation cleanup failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}."); return 1; }
                await Task.Delay(1000);
            }
        }
        return 1;
    }

    private static async Task CompleteBootRegistrationAsync(string root, DiagnosticLog log)
    {
        // Complete Burn registration after verified early-boot file replacement.
        string directory = Path.Combine(root, ".setup", "registration");
        if (!Directory.Exists(directory)) return;
        CheckDirectory(Path.Combine(root, ".setup")); CheckDirectory(directory);
        string installer = Path.Combine(directory, "setup.exe"), checksum = Path.Combine(directory, "sha256.txt");
        foreach (string path in new[] { installer, checksum })
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid boot registration file.");
        if (new FileInfo(checksum).Length != 64) throw new IOException("Invalid boot registration checksum.");
        using (var stream = File.OpenRead(installer))
            if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(File.ReadAllText(checksum), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Boot registration installer checksum mismatch.");
        log.Write("Completing boot installation registration without stopping the service.");
        var start = new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "-quiet", "-norestart", "Scope=PerMachine", "RegistrationOnly=1", "Mode=service" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not complete boot registration.");
        using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Boot registration has not finished; its installer is still running and recovery files are retained."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("Boot registration failed with exit code " + process.ExitCode + "; recovery files are retained.");
        File.Delete(installer); File.Delete(checksum); Directory.Delete(directory);
        log.Write("Boot installation registration completed.");
    }
}
