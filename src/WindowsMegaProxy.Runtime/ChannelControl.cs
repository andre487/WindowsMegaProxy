using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class ChannelControl
{
    public const int ActivateMessage = 0x8488;
    public const int ExitMessage = 0x8487;
    public static string OtherName => AppIdentity.IsDev ? "WindowsMegaProxy" : "WindowsMegaProxy.Dev";
    public static string OtherDisplayName => AppIdentity.IsDev ? "MegaProxy" : "MegaProxy DEV";

    public static bool OtherRunning() => Running(OtherName) || Running(OtherName + ".Service");

    public static bool CurrentServiceRunning()
    {
        try { using var service = new ServiceController(AppIdentity.Name); return service.Status != ServiceControllerStatus.Stopped; }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 }) { return false; }
    }

    private static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static bool ActivateWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        AllowSetForegroundWindow(pid);
        return SendMessageTimeout(window, ActivateMessage, IntPtr.Zero, IntPtr.Zero, 2, 1000, out var reply) != IntPtr.Zero && reply == new IntPtr(0x487);
    }

    public static void ActivateExisting()
    {
        var processes = Process.GetProcessesByName(AppIdentity.Name);
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var ids = processes.Where(process => process.Id != Environment.ProcessId && process.SessionId == session).Select(process => process.Id).ToHashSet();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out uint pid);
                return !ids.Contains((int)pid) || !ActivateWindow(window);
            }, IntPtr.Zero);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static Task ResolveAsync(bool stopOther) => Task.Run(() =>
    {
        if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("Stopping a MegaProxy host requires administrator approval.");
        string name = stopOther ? OtherName : AppIdentity.Name;
        if (stopOther)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                int session = Process.GetCurrentProcess().SessionId;
                if (processes.Any(process => process.SessionId != session)) throw new IOException("The other MegaProxy version is open in another Windows session. Close it there first.");
                foreach (var process in processes)
                {
                    EnumWindows((window, _) =>
                    {
                        GetWindowThreadProcessId(window, out uint pid);
                        if (pid == process.Id) SendMessageTimeout(window, ExitMessage, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _);
                        return true;
                    }, IntPtr.Zero);
                    if (!process.WaitForExit(30000)) throw new IOException("The other MegaProxy window did not close. Finish its pending operation and retry.");
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        try
        {
            using var service = new ServiceController(name);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
        if (stopOther)
            for (int attempt = 0; OtherRunning(); attempt++)
            {
                if (attempt == 60) throw new IOException("The other MegaProxy host is still exiting. Retry after it stops.");
                System.Threading.Thread.Sleep(500);
            }
    });

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
