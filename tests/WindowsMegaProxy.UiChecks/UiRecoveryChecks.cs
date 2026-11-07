using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsMegaProxy;

internal static class UiRecoveryChecks
{
    public static async Task RunIsolatedAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(UiRecoveryChecks).Assembly.Location);
        start.ArgumentList.Add("--ui-recovery-check");
        var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(40), default);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error + result.Output);
        Console.Write(result.Output);
    }
    public static void Run()
    {
        string root = Path.GetFullPath("artifacts/ui-recovery/" + Guid.NewGuid().ToString("N"));
        var store = new MachineSettingsStore(root, machine: false);
        string pipe = "WindowsMegaProxy.UiRecovery." + Guid.NewGuid().ToString("N");
        var client = new ServiceClient(pipe, WindowsIdentity.GetCurrent().User!.Value);
        var config = new Settings { Language = "en" };
        var profile = ProxyProfile.Create("Recovered profile"); profile.Proxy["host"] = "proxy.invalid";
        config.Profiles.Add(profile); config.ActiveProfileId = profile.Id;
        int migrations = 0;
        var window = new MainWindow(false, diagnosticLog: new DiagnosticLog(Path.Combine(root, "ui-log")),
            serviceClient: client, loadLegacy: () => { migrations++; return Task.FromResult(config.Copy()); }, diagnosticsOnEnter: () => Task.CompletedTask);
        var timer = (DispatcherTimer)Field(window, "timer")!;
        timer.Stop();
        try
        {
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Settings { Language = "en" }]);
            Wait(window, (Task)Field(window, "analysisTask")!);
            Wait(window, Invoke(window, "RefreshServiceAsync"));
            Assert(((TextBlock)window.FindName("StatusText")).Text == "Service unavailable" && migrations == 0,
                "An unavailable service cannot overwrite or prematurely migrate settings.");
            var first = new RecoveryFixture(store, pipe);
            try
            {
                Wait(window, Invoke(window, "RefreshServiceAsync"));
                Assert(migrations == 1 && ((Settings)Field(window, "settings")!).ActiveProfile!.Name == "Recovered profile",
                    "First recovery migrates profiles through the same initialization path as normal startup.");
                var remote = new RemoteEngine(client, _ => Task.FromResult(false));
                Wait(window, remote.StartAsync(config));
                Wait(window, Invoke(window, "RefreshServiceAsync"));
                Assert(((TextBlock)window.FindName("StatusText")).Text == "Connected", "The recovered UI attaches to the live runtime.");
            }
            finally { Wait(window, first.DisposeAsync().AsTask()); }
            Wait(window, Invoke(window, "RefreshServiceAsync"));
            var tray = (System.Windows.Forms.NotifyIcon)Field(window, "tray")!;
            Assert(tray.Text == "MegaProxy — service unavailable" && !((IEngine)((ConnectionController)Field(window, "connection")!).Engine!).Running,
                "Service failure invalidates the remote runtime and clears the connected tray indication.");
            Assert(((DataGrid)window.FindName("ConnectionsGrid")).Items.Count == 0 &&
                !((Button)window.FindName("ConnectButton")).IsEnabled, "Service failure clears stale rows and disables control.");
            var second = new RecoveryFixture(store, pipe);
            try
            {
                var newer = config.Copy(); newer.Profiles[0].Data["name"] = "New service profile";
                Wait(window, StoreSave(second, newer));
                Wait(window, Invoke(window, "RefreshServiceAsync"));
                Assert(migrations == 1 && ((Settings)Field(window, "settings")!).ActiveProfile!.Name == "New service profile",
                    "Rebinding loads replacement settings even when its counters overlap the old instance.");
                Assert(((Button)window.FindName("ConnectButton")).IsEnabled && tray.Text == "MegaProxy — disconnected",
                    "A successful handshake restores editable controls and a truthful tray status.");
            }
            finally { Wait(window, second.DisposeAsync().AsTask()); }
        }
        finally { Wait(window, Invoke(window, "QuitAsync")); }
        Console.WriteLine("UI service loss/rebind, migration, controls, tray and stale-row cleanup passed.");
    }

    private static async Task StoreSave(RecoveryFixture fixture, Settings settings)
    {
        var peer = new ClientAccess("fixture-admin", true);
        await fixture.Broker.HandleAsync(peer, new("save", Settings: settings, Revision: 0, InstanceId: fixture.Broker.InstanceId), default);
    }
    private static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static Task Invoke(MainWindow window, string name) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static void Wait(Window window, Task task)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            System.Threading.Thread.Sleep(1);
        }
        Assert(task.IsCompleted, "Recovery must complete without blocking Dispatcher.");
        task.GetAwaiter().GetResult();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
