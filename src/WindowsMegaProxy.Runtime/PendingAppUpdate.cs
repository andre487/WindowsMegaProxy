using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public sealed record PendingAppUpdate(string Version, string When, int? WorkerPid = null, long? WorkerStartedUtcTicks = null)
{
    public const string RegistryValue = "PendingUpdate";
    public static string TaskName => AppIdentity.Name + ".Update";

    public static string ValidateAction(string action, bool service) => action == "now" ||
        (service ? action is "restart" or "reboot" : action == "launch") ? action : throw new ArgumentException("Invalid update timing for this operating mode.");

    public static PendingAppUpdate? Read()
    {
        try { return ReadCore(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogRecovery("Cannot read update status registration: " + error.Message);
            return new(AppUpdateTrust.CurrentVersion.ToString(3), "unknown");
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidDataException)
        {
            LogRecovery("Invalid pending update registration; manual recovery is available: " + error.Message);
            return null;
        }
    }

    public static void LogRecovery(string message)
    {
        try
        {
            string directory = StartupConfiguration.Mode == "service" ? SettingsStore.DirectoryPath : StartupConfiguration.LocalDirectory;
            new DiagnosticLog(Path.Combine(directory, "logs"), initialLimit: 100).Write(message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceError(DiagnosticLog.Redact(message + "; logging failed: " + error.Message)); }
    }

    private static PendingAppUpdate? ReadCore()
    {
        if (AppIdentity.IsDev) return null;
        bool personal = UserInstallation.IsUser;
        using var key = (personal ? Registry.CurrentUser : Registry.LocalMachine)
            .OpenSubKey(personal ? UserInstallation.RegistryPath : StartupConfiguration.RegistryPath);
        if (key?.GetValue(RegistryValue) is not string value) return null;
        if (value.Length > 4096) throw new InvalidDataException("Invalid pending update record.");
        var pending = JsonSerializer.Deserialize<PendingAppUpdate>(value) ?? throw new InvalidDataException("Invalid pending update record.");
        if (string.IsNullOrEmpty(pending.Version)) throw new InvalidDataException("Missing pending update version.");
        _ = AppUpdateTrust.ParseVersion(pending.Version);
        if (pending.When is not ("now" or "launch" or "reboot" or "applying" or "failed")) throw new InvalidDataException("Invalid pending update timing.");
        return Recover(pending, AppUpdateTrust.CurrentVersion, pending.When is "now" or "applying" ? WorkerRunning(pending) : false);
    }

    public static PendingAppUpdate? Recover(PendingAppUpdate pending, Version installed, bool? workerRunning) =>
        pending.When is "now" or "applying" ? pending with { When = workerRunning == true ? pending.When : workerRunning == false ? "failed" : "unknown" } :
        pending.When == "failed" || AppUpdateTrust.IsNewer(pending.Version, installed) ? pending : null;

    public static bool? WorkerRunning(PendingAppUpdate pending) => CheckWorker(pending, pid =>
    {
        using var process = Process.GetProcessById(pid);
        return process.StartTime.ToUniversalTime().Ticks;
    });

    private static bool? CheckWorker(PendingAppUpdate pending, Func<int, long?> started)
    {
        if (pending.WorkerPid is not > 0 || pending.WorkerStartedUtcTicks is not > 0) return null;
        try { return started(pending.WorkerPid.Value) == pending.WorkerStartedUtcTicks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
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
