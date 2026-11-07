using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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

    private static async Task<int> InstallUpdateAsync()
    {
        if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("Update installation requires administrator approval.");
        if (AppIdentity.IsDev) throw new InvalidOperationException("Use run.cmd to update development builds.");
        bool personal = UserInstallation.IsUser;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string root = personal ? UserInstallation.ValidateDirectory(AppContext.BaseDirectory, programFiles, UserInstallation.Sid) : InstallRoot(AppContext.BaseDirectory, programFiles);
        foreach (string path in new[] { root, Path.Combine(root, "versions"), AppContext.BaseDirectory }) CheckDirectory(path);
        string staging = Path.Combine(root, "update");
        ProtectDataDirectory(staging);
        using var lease = new FileStream(Path.Combine(staging, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string target = Path.Combine(staging, "setup.exe");
        try
        {
            string cache = AppUpdateTrust.CacheDirectory;
            AppUpdateTrust.CheckDirectory(cache);
            string statePath = Path.Combine(cache, "state.json");
            if (new FileInfo(statePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Invalid update state.");
            var state = JsonSerializer.Deserialize<AppUpdateState>(await File.ReadAllTextAsync(statePath)) ?? throw new InvalidDataException("Missing update state.");
            var package = AppUpdateTrust.Verify(state.Manifest ?? [], state.Signature ?? [], AppUpdateTrust.PublicKey);
            if (!AppUpdateTrust.IsNewer(package.Version, AppUpdateTrust.CurrentVersion)) throw new InvalidDataException("Updates cannot downgrade or reinstall this version.");
            // Copy into an administrator-only directory; never elevate a user-writable executable.
            await using (var input = new FileStream(Path.Combine(cache, "setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (input.Length != package.Size) throw new CryptographicException("Invalid update installer size.");
                await input.CopyToAsync(output);
            }
            await AppUpdateTrust.VerifyInstallerAsync(target, package, CancellationToken.None);
            Settings current = personal ? new MachineSettingsStore(UserInstallation.DataDirectory, machine: false).Load() : new MachineSettingsStore().Load();
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            foreach (string argument in new[] { "/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES", personal ? "/CURRENTUSER" : "/ALLUSERS",
                "/MODE=" + StartupConfiguration.Mode, "/UPDATE=1", "/MERGETASKS=" + (AppUpdatePolicy.Enabled(current) ? "automaticupdates" : "!automaticupdates") }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the verified update installer.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("The update installer did not complete successfully.");
            return 0;
        }
        finally { File.Delete(target); }
    }
}
