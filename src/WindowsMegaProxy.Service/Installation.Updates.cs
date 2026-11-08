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
                        string worker = Path.Combine(staging, "worker");
                        if (Directory.Exists(worker)) { CheckDirectory(worker); Directory.Delete(worker, true); }
                        string setup = Path.Combine(staging, "setup.exe");
                        if (File.Exists(setup)) File.Delete(setup);
                    }
                }
                // The 0.0.4 update helper exits only after setup; wait for it to release the old EXE.
                string legacy = Path.Combine(root, "versions");
                if (Directory.Exists(legacy)) { CheckDirectory(legacy); Directory.Delete(legacy, true); }
                log.Write("Installation cleanup completed; only the current application version is retained.");
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
}
