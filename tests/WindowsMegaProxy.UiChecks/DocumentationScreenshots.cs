using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsMegaProxy;

internal static partial class Program
{
    private static void CaptureDocumentation()
    {
        if (AppIdentity.IsDev) throw new InvalidOperationException("Documentation requires a production build.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        string output = Path.GetFullPath("docs/screenshots");
        string scratch = Path.GetFullPath("artifacts/documentation-screenshots/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var controller = new ConnectionController(() => new TestEngine());
        // Inject every startup dependency so screenshots never load personal settings or use the network.
        var window = new MainWindow(false, _ => { }, new DiagnosticLog(Path.Combine(scratch, "logs")),
            _ => Task.CompletedTask, () => Task.FromResult(false), () => Task.CompletedTask, controller,
            loadLegacy: () => Task.FromResult(new Settings()))
        { Left = -10000, Top = -10000, ShowInTaskbar = false, Width = 1200, Height = 1150 };
        object? Invoke(string name, params object?[] values) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, values);
        window.Loaded -= typeof(MainWindow).GetMethod("WindowLoaded", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<RoutedEventHandler>(window);
        ((DispatcherTimer)typeof(MainWindow).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
        var settings = new Settings { Language = "en", Theme = "light", ShowAllConnections = true };
        foreach (var (name, country, type, host, port) in new[]
        {
            ("Berlin", "DE", "HTTPS", "de.proxy.example", 443),
            ("Amsterdam", "NL", "SOCKS5", "nl.proxy.example", 1080),
            ("Stockholm", "SE", "SSH", "se.proxy.example", 22),
            ("Paris", "FR", "MASQUE", "fr.proxy.example", 443)
        })
        {
            var profile = ProxyProfile.Create(name);
            profile.Data["countryCode"] = country;
            profile.Proxy["type"] = type; profile.Proxy["host"] = host; profile.Proxy["port"] = port;
            settings.Profiles.Add(profile);
        }
        settings.ActiveProfileId = settings.Profiles[0].Id;
        settings.Rules =
        [
            new() { Name = "Web browser", Source = "exe", Path = @"C:\Apps\Browser\browser.exe", Action = "proxy" },
            new() { Name = "Work tools", Source = "folder", Path = @"C:\Apps\Work", Destination = "*.example.com", Action = "proxy" },
            new() { Name = "Local network", Destination = "192.168.0.0/16", Action = "system" },
            new() { Name = "Blocked destinations", Destination = "blocked.example", Action = "block" },
            new() { Name = "Optional rule", Destination = "optional.example", Action = "proxy", Enabled = false }
        ];
        try
        {
            Invoke("ApplySettings", settings);
            window.Show(); Layout(window);
            Wait(window, (Task)typeof(MainWindow).GetField("analysisTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!);
            Wait(window, controller.ConnectAsync(settings));
            Invoke("SetConnected", true);
            ((TextBlock)window.FindName("ConnectionsHint")).Text = "Shown: 4 of 4.";
            var columns = ((DataGrid)window.FindName("ConnectionsGrid")).Columns;
            double[] widths = [260, 230, 95, 145, 135, 135];
            for (int index = 0; index < widths.Length; index++) columns[index].Width = new DataGridLength(widths[index]);
            var sections = (TabControl)window.FindName("Sections");
            foreach (string theme in new[] { "light", "dark" })
            {
                UiTheme.Apply("en", theme, window);
                Wait(window, (Task)Invoke("NavigateSettingsAsync", new object?[] { null })!);
                Invoke("UpdateConnections", (object)new ConnectionRow[]
                {
                    new(@"C:\Apps\Browser\browser.exe", "www.example.com:443", "TCP", "Berlin", "128 KiB", "8.4 MiB", 0),
                    new(@"C:\Apps\Work\client.exe", "api.example.com:443", "TCP", "Berlin", "32 KiB", "1.2 MiB", 0),
                    new(@"C:\Apps\Browser\browser.exe", "blocked.example:443", "TCP", "Blocked", "0 B", "0 B", 1),
                    new(@"C:\Windows\System32\svchost.exe", "192.168.1.1:53", "UDP", "System", "2 KiB", "6 KiB", 2)
                });
                string[] names = ["connections", "profiles", "rules", "settings", "diagnostics", "log"];
                for (int index = 0; index < names.Length; index++)
                {
                    sections.SelectedIndex = index;
                    if (index == 4)
                    {
                        Invoke("CompleteDiagnostics", new DiagnosticResult("203.0.113.42", "DE", 124, ""), settings);
                        Invoke("SetBusy", false);
                        ((TextBox)window.FindName("TestLogText")).Text = "HTTPS check completed successfully.";
                    }
                    if (index == 5)
                    {
                        Layout(window);
                        ((TextBox)window.FindName("LogText")).Text = "2026-11-07 12:00:00  Application started.\n2026-11-07 12:00:01  Configuration loaded: 4 profiles, 5 rules.\n2026-11-07 12:00:02  Active profile: Berlin.\n2026-11-07 12:00:02  TUN connected.\n2026-11-07 12:00:05  HTTPS check completed successfully.";
                    }
                    Layout(window);
                    Assert(((TextBlock)window.FindName("AppTitle")).Text == "MegaProxy", "Documentation must not show DEV branding.");
                    Capture((Visual)window.Content, Path.Combine(output, names[index] + "-" + theme + ".png"));
                }
            }
        }
        finally { Wait(window, (Task)Invoke("QuitAsync")!); }
        Console.WriteLine("Documentation screenshots captured with synthetic data only; no service or TUN was started.");
    }
}
