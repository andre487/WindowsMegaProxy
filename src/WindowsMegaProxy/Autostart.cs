using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WindowsMegaProxy;

public static class Autostart
{
    private static string TaskName => "WindowsMegaProxy-" + WindowsIdentity.GetCurrent().User!.Value;

    public static async Task<bool> IsEnabledAsync() => await RunAsync("/Query", "/TN", TaskName) == 0;

    public static async Task SetAsync(bool enabled)
    {
        if (!enabled)
        {
            if (await IsEnabledAsync() && await RunAsync("/Delete", "/TN", TaskName, "/F") != 0)
                throw new InvalidOperationException(Ui.T("Could not disable startup."));
            return;
        }
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var path = Environment.ProcessPath ?? throw new InvalidOperationException(Ui.T("Application path not found."));
        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", true), new XElement(ns + "UserId", sid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                new XElement(ns + "UserId", sid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false), new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), new XElement(ns + "Exec",
                new XElement(ns + "Command", path), new XElement(ns + "Arguments", "--tray"),
                new XElement(ns + "WorkingDirectory", AppContext.BaseDirectory))));
        var xml = Path.Combine(SettingsStore.DirectoryPath, "autostart.xml");
        try
        {
            new XDocument(new XDeclaration("1.0", "utf-8", null), task).Save(xml);
            if (await RunAsync("/Create", "/TN", TaskName, "/XML", xml, "/F") != 0)
                throw new InvalidOperationException(Ui.T("Could not enable startup in Task Scheduler."));
        }
        finally { File.Delete(xml); }
    }

    private static async Task<int> RunAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(Ui.T("Could not start Task Scheduler."));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }
}
