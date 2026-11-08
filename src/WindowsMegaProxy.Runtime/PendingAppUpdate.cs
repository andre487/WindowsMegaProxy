using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public sealed record PendingAppUpdate(string Version, string When)
{
    public const string RegistryValue = "PendingUpdate";
    public static string TaskName => AppIdentity.Name + ".Update";

    public static string ValidateAction(string action, bool service) => action == "now" ||
        (service ? action is "restart" or "reboot" : action == "launch") ? action : throw new ArgumentException("Invalid update timing for this operating mode.");

    public static PendingAppUpdate? Read()
    {
        if (AppIdentity.IsDev) return null;
        bool personal = UserInstallation.IsUser;
        using var key = (personal ? Registry.CurrentUser : Registry.LocalMachine)
            .OpenSubKey(personal ? UserInstallation.RegistryPath : StartupConfiguration.RegistryPath);
        if (key?.GetValue(RegistryValue) is not string value) return null;
        if (value.Length > 4096) throw new InvalidDataException("Invalid pending update record.");
        var pending = JsonSerializer.Deserialize<PendingAppUpdate>(value) ?? throw new InvalidDataException("Invalid pending update record.");
        _ = AppUpdateTrust.ParseVersion(pending.Version);
        if (pending.When is not ("now" or "launch" or "reboot" or "applying")) throw new InvalidDataException("Invalid pending update timing.");
        return pending.When == "applying" || AppUpdateTrust.IsNewer(pending.Version, AppUpdateTrust.CurrentVersion) ? pending : null;
    }

    public static bool WorkerRunning()
    {
        try
        {
            using var lease = new FileStream(Path.Combine(AppContext.BaseDirectory, "update", "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { return true; }
    }

    public static async Task RequestAsync(string action, bool applyPending = false)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable);
        if (!File.Exists(helper)) throw new IOException("The protected update installer helper is missing.");
        var start = new ProcessStartInfo(helper)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(applyPending ? "--apply-pending-update" : "--install-update");
        if (!applyPending) start.ArgumentList.Add(action);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Could not prepare the application update. See the application log for details.");
    }
}
