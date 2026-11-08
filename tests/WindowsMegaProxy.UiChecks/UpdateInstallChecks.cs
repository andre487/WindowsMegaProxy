using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsMegaProxy;

internal static class UpdateInstallChecks
{
    public static async Task RunIsolatedAsync()
    {
        if (AppIdentity.IsDev) return;
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(UpdateInstallChecks).Assembly.Location);
        start.ArgumentList.Add("--update-install-check");
        var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(40), default);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error + result.Output);
        Console.Write(result.Output);
    }

    public static void Run()
    {
        string root = Path.GetFullPath("artifacts/update-install/" + Guid.NewGuid().ToString("N"));
        using var feed = new AppUpdateChecks.Feed();
        using var manager = feed.Manager(Path.Combine(root, "cache"));
        manager.CheckAsync(default).GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml") });
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var window = new MainWindow(false, persistSettings: _ => { }, loadLegacy: () => Task.FromResult(new Settings { Language = "en" }),
            diagnosticLog: new DiagnosticLog(Path.Combine(root, "logs")), diagnosticsOnEnter: () => Task.CompletedTask,
            appUpdates: manager, installAppUpdate: () => { starts++; return handoff.Task; });
        bool Field(string name) => (bool)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var deadline = Stopwatch.StartNew();
        Exception? failure = null;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new Exception("Update handoff did not finish.");
                if (Field("settingsLoading") || !Field("settingsReady") || starts != 0) return;
                var install = (Button)window.FindName("InstallUpdateButton");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (starts != 1 || !Field("installingAppUpdate") || install.IsEnabled) throw new Exception("Installation must disable repeat launches.");
                install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (starts != 1) throw new Exception("Repeated clicks must not launch another installer.");
                handoff.SetResult();
            }
            catch (Exception error) { failure = error; app.Shutdown(1); }
        };
        timer.Start();
        app.Run(window);
        timer.Stop();
        if (failure is not null) throw failure;
        if (starts != 1 || !Field("quitting")) throw new Exception("A successful handoff must shut down the UI and release its DLLs.");
        Console.WriteLine("Update handoff exits the UI and rejects duplicate installation clicks; no installer or TUN was started.");
    }
}
