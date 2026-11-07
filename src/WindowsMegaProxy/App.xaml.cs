using System;
using System.Threading;
using System.Windows;
using System.Globalization;

namespace WindowsMegaProxy;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetCurrentProcessExplicitAppUserModelID(AppIdentity.Name);
        instance = new Mutex(true, AppIdentity.MutexName, out bool created);
        if (!created)
        {
            MessageBox.Show(Ui.T("MegaProxy is already running. Open it from the system tray."), "MegaProxy");
            Shutdown();
            return;
        }
        try
        {
            var window = new MainWindow(Array.Exists(e.Args, arg => arg == "--tray"));
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
        base.OnExit(e);
    }
}
