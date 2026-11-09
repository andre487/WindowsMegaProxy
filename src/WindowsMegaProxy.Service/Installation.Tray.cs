using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowsMegaProxy;

public static partial class Installation
{
    private static void ConfigureTray(string mode)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        string name = AppIdentity.Name + ".Tray";
        if (mode == "service")
        {
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "Restore the unelevated MegaProxy tray client after a boot update.";
            task.Principal.GroupId = "S-1-5-32-545";
            task.Principal.RunLevel = 0;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 0;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            dynamic action = task.Actions.Create(0);
            action.Path = Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable);
            action.Arguments = "--tray";
            action.WorkingDirectory = AppContext.BaseDirectory;
            folder.RegisterTaskDefinition(name, task, 6, "S-1-5-32-545", null, 4);
        }
        else
        {
            try { folder.DeleteTask(name, 0); }
            catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { }
        }
        using var startup = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        if (mode == "service") startup.SetValue(ServiceProtocol.Name, $"\"{Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable)}\" --tray");
        else startup.DeleteValue(ServiceProtocol.Name, false);
    }

    private static void RestoreTrayClients(DiagnosticLog log)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out int count)) throw new Win32Exception();
        try
        {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            scheduler.Connect();
            dynamic task = scheduler.GetFolder(@"\").GetTask(AppIdentity.Name + ".Tray");
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<TraySession>(IntPtr.Add(sessions, i * Marshal.SizeOf<TraySession>()));
                if (session.Id <= 0 || session.State is not (0 or 4)) continue;
                try
                {
                    task.RunEx(null, 4, session.Id, null);
                    log.Write($"Requested the unelevated tray client in Windows session {session.Id} after installation.");
                }
                catch (Exception error) { log.Write($"Could not restore the tray client in session {session.Id}: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}."); }
            }
        }
        finally { WTSFreeMemory(sessions); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraySession { public int Id; public IntPtr Station; public int State; }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
}
