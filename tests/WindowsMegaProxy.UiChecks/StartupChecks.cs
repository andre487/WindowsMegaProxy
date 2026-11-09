using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsMegaProxy;

internal static class StartupChecks
{
    public static async Task RunIsolatedAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(StartupChecks).Assembly.Location);
        start.ArgumentList.Add("--startup-check");
        var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(30), default);
        if (result.ExitCode != 0) throw new Exception(result.Error + result.Output);
        Console.Write(result.Output);
    }

    public static void Run()
    {
        int loads = 0;
        var loaded = new TaskCompletionSource<Settings>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(false, standalone: true,
            loadLegacy: () => { loads++; return loaded.Task; }, getAutostart: () => Task.FromResult(false),
            diagnosticLog: new DiagnosticLog(Path.GetFullPath("artifacts/startup-checks/" + Guid.NewGuid().ToString("N"))))
            { Left = -10000, Top = -10000, ShowInTaskbar = false };
        ((DispatcherTimer)Field(window, "timer")).Stop();
        try
        {
            Check(loads == 1 && !window.IsLoaded, "Settings start loading before the window is shown.");
            window.Show();
            Pump(window);
            Check(window.IsLoaded && !((TabControl)window.FindName("Sections")).IsEnabled, "The window can render while settings are still loading.");
            loaded.SetResult(new Settings { Language = "en", Theme = "light" });
            Wait(window, () => ((TabControl)window.FindName("Sections")).IsEnabled);
            Check(loads == 1 && Ui.Language == "en", "Startup reuses the prefetched settings and applies their language.");
            UiTheme.Apply("ru", "light", window); Pump(window);
            int dictionaries = Application.Current.Resources.MergedDictionaries.Count;
            long before = GC.GetAllocatedBytesForCurrentThread();
            UiTheme.Apply("en", "light", window); Pump(window);
            Check(GC.GetAllocatedBytesForCurrentThread() - before < 10 * 1024 * 1024,
                "Changing language replaces one resource dictionary instead of invalidating the entire UI for each string.");
            Check(Application.Current.Resources.MergedDictionaries.Count == dictionaries && window.FindResource("Connect") as string == "Connect",
                "Replacing language resources does not accumulate dictionaries or leave stale translations.");
            Console.WriteLine("Nonblocking startup, one settings load and batched language resources passed.");
        }
        finally
        {
            loaded.TrySetResult(new Settings());
            var quit = (Task)typeof(MainWindow).GetMethod("QuitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Wait(window, () => quit.IsCompleted);
            quit.GetAwaiter().GetResult();
        }
    }

    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void Pump(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Window window, Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done() && timer.Elapsed < TimeSpan.FromSeconds(10)) { Pump(window); System.Threading.Thread.Sleep(1); }
        Check(done(), "Startup must remain responsive and finish its tracked work.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
