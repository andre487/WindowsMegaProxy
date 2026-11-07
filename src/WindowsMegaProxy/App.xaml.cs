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
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID(AppIdentity.Name);
        bool standalone = StartupConfiguration.Mode != "service";
        if (standalone && !StartupConfiguration.IsElevated)
        {
            try
            {
                if (UserInstallation.IsUser) { UserInstallation.Launch(); Shutdown(); return; }
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
            MessageBox.Show(Ui.T("MegaProxy is already running. Open it from the system tray."), "MegaProxy");
            Shutdown();
            return;
        }
        try
        {
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
