using System;
using System.Threading;
using System.Windows;
using System.Globalization;
using System.Diagnostics;

namespace WindowsMegaProxy;

public partial class App : Application
{
    private Mutex? instance;
    private IDisposable? runtime;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID(AppIdentity.Name);
        if (e.Args.Length == 2 && e.Args[0] == "--resolve-channel-conflict")
        {
            try
            {
                Ui.SetLanguage("en");
                if (e.Args[1] is not ("stop-other" or "exit-current")) throw new ArgumentException("Invalid channel switch command.");
                await ChannelControl.ResolveAsync(e.Args[1] == "stop-other");
                Shutdown();
            }
            catch (Exception error)
            {
                var native = error as System.ComponentModel.Win32Exception ?? error.InnerException as System.ComponentModel.Win32Exception;
                string detail = native is null ? error.Message : $"Windows error {native.NativeErrorCode}.";
                try { new DiagnosticLog(System.IO.Path.Combine(SettingsStore.DirectoryPath, "logs"), initialLimit: 100).Write($"Channel switch {e.Args[1]} failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}; {detail}"); }
                catch (System.IO.IOException) { }
                catch (UnauthorizedAccessException) { }
                Shutdown(1);
            }
            return;
        }
        bool alreadyRunning;
        try
        {
            alreadyRunning = Mutex.TryOpenExisting(AppIdentity.MutexName, out var existing);
            existing?.Dispose();
        }
        catch (UnauthorizedAccessException) { alreadyRunning = true; }
        if (alreadyRunning)
        {
            if (!Array.Exists(e.Args, argument => argument == "--tray")) ChannelControl.ActivateExisting();
            Shutdown();
            return;
        }
        bool standalone;
        try { standalone = StartupConfiguration.Mode != "service"; }
        catch (Exception error)
        {
            MessageBox.Show(Ui.T("Could not open the application: ") + error.Message, "MegaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (standalone && !StartupConfiguration.IsElevated)
        {
            try
            {
                if (UserInstallation.IsUser)
                    throw new UnauthorizedAccessException("The scheduled user application requires administrator privileges. Use its launcher or repair the installation.");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (string argument in e.Args) start.ArgumentList.Add(argument);
                Process.Start(start)?.Dispose();
            }
            catch (Exception error) { MessageBox.Show(Ui.T("Could not open the application: ") + error.Message, "MegaProxy"); }
            Shutdown();
            return;
        }
        instance = new Mutex(true, AppIdentity.MutexName, out bool created);
        if (!created)
        {
            if (!Array.Exists(e.Args, argument => argument == "--tray")) ChannelControl.ActivateExisting();
            Shutdown();
            return;
        }
        try
        {
            var pending = AppIdentity.IsDev ? null : PendingAppUpdate.Read();
            if (pending?.When is "applying" or "now" && PendingAppUpdate.WorkerRunning()) { Shutdown(); return; }
            if (standalone && pending?.When == "launch")
            {
                try
                {
                    await PendingAppUpdate.RequestAsync("now", applyPending: true);
                    Shutdown();
                    return;
                }
                catch (Exception error)
                {
                    PendingAppUpdate.LogRecovery("Could not apply the next-start update; opening the recovery interface: " + error.Message);
                }
            }
            if (standalone) runtime = StartupConfiguration.ClaimRuntime();
            var window = new MainWindow(Array.Exists(e.Args, arg => arg == "--tray"), standalone: standalone);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(Ui.T("Could not open the application: ") + error.Message, "MegaProxy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        runtime?.Dispose();
        base.OnExit(e);
    }
}
