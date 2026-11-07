using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using WindowsMegaProxy;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        InstallationChecks.Run();
        if (args.Length == 2 && args[0] == "--installed-settings-check")
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") throw new Exception("Installed settings fixtures require a disposable CI runner.");
            var store = new MachineSettingsStore();
            if (args[1] == "seed")
            {
                var profile = ProxyProfile.Create("Installer preservation check");
                profile.Proxy["host"] = "proxy.invalid";
                profile.Proxy["password"] = "synthetic-secret";
                store.Save(new Settings { Profiles = [profile], AutoConnect = false });
            }
            var installedSettings = store.Load();
            if (installedSettings.Profiles.Count != 1 || installedSettings.Profiles[0].Name != "Installer preservation check" || installedSettings.Profiles[0].Proxy["password"]?.ToString() != "synthetic-secret")
                throw new Exception("Settings were not preserved across operating mode changes.");
            return;
        }
        if (CoreProcessChecks.RunFixture(args)) return;
        if (RecoveryChecks.RunFixture(args)) return;
        if (args.Contains("--ui-recovery-check"))
        {
            var recoveryApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(recoveryApp.Dispatcher));
            recoveryApp.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml") });
            UiRecoveryChecks.Run();
            return;
        }
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        if (args.Length == 2 && args[0] == "--ssh-check") { SshChecks.RunAsync(args[1]).GetAwaiter().GetResult(); return; }
        if (args.Contains("--http3-check")) { Http3Checks.RunAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--http3-network-check")) { Http3Checks.RunAsync(network: true).GetAwaiter().GetResult(); return; }
        if (args.Contains("--engine-route-check"))
        {
            try { CheckProbeRouteAsync().GetAwaiter().GetResult(); }
            catch (Exception error) { File.WriteAllText("artifacts/engine-route-check/failure.txt", error.ToString()); throw; }
            return;
        }
        if (args.Contains("--engine-check")) { CheckEngineAsync().GetAwaiter().GetResult(); return; }
        ControllerChecks.RunAsync().GetAwaiter().GetResult();
        CoreProcessChecks.RunAsync().GetAwaiter().GetResult();
        PerformanceChecks.RunAsync().GetAwaiter().GetResult();
        ServiceChecks.RunAsync().GetAwaiter().GetResult();
        RecoveryChecks.RunAsync().GetAwaiter().GetResult();
        UiRecoveryChecks.RunIsolatedAsync().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
        var saved = new System.Collections.Generic.List<Settings>();
        bool rejectSave = false;
        bool slowSave = false;
        using var releaseSave = new System.Threading.ManualResetEventSlim();
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int diagnosticEntries = 0;
        bool autostartEnabled = false, rejectAutostart = false;
        var fixtureEngine = new TestEngine();
        var testConnection = new ConnectionController(() => fixtureEngine);
        var window = new MainWindow(false, value =>
        {
            if (slowSave)
            {
                slowSave = false;
                saveStarted.TrySetResult();
                if (!releaseSave.Wait(TimeSpan.FromSeconds(10))) throw new IOException("UI did not release the synthetic disk write.");
            }
            if (rejectSave) throw new IOException("Synthetic save failure.");
            saved.Add(PortableConfiguration.Copy(value));
        }, new DiagnosticLog(Path.GetFullPath(AppIdentity.IsDev ? "artifacts/ui-checks-dev/logs" : "artifacts/ui-checks/logs")), enabled =>
        {
            if (rejectAutostart && enabled) throw new IOException("Synthetic task failure.");
            autostartEnabled = enabled;
            return Task.CompletedTask;
        }, () => Task.FromResult(autostartEnabled), () => { diagnosticEntries++; return Task.CompletedTask; }, testConnection) { Left = -10000, Top = -10000, ShowInTaskbar = false };
        // Exercise layout without executing startup tasks, autoconnect or any network operations.
        window.Loaded -= typeof(MainWindow).GetMethod("WindowLoaded", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<RoutedEventHandler>(window);
        Assert(!((TabControl)window.FindName("Sections")).IsEnabled && !((Button)window.FindName("ConnectButton")).IsEnabled,
            "Startup cannot edit or overwrite settings before the saved configuration has loaded.");
        try
        {
            string build = ((System.Windows.Documents.Run)window.FindName("BuildInformationText")).Text;
            string version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert(build.StartsWith($"v{version} · ") && build.Length >= 16, "Footer shows the version and source revision.");
            Assert(window.Title == AppIdentity.DisplayName && ((TextBlock)window.FindName("AppTitle")).Text == AppIdentity.DisplayName, "The build's window and header identify its channel.");
            var access = typeof(MainWindow).GetField("canEdit", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var setBusy = typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!;
            access.SetValue(window, false);
            ((TabControl)window.FindName("Sections")).IsEnabled = true;
            setBusy.Invoke(window, [false]);
            Assert(((System.Windows.Documents.Run)window.FindName("ReadOnlyModeText")).Text == " · Read-only mode", "Read-only label follows the GitHub link.");
            foreach (string name in new[] { "ConnectButton", "ReconnectButton", "ProfilePicker", "DefaultActionBox", "BypassLanBox", "AutostartBox", "LanguageBox", "TestConnectionButton" })
                Assert(!((Control)window.FindName(name)).IsEnabled, "Read-only control: " + name);
            Assert(((DataGrid)window.FindName("RulesList")).IsEnabled, "Read-only rules remain selectable for copying.");
            access.SetValue(window, true);
            ((TabControl)window.FindName("Sections")).IsEnabled = false;
            setBusy.Invoke(window, [false]);
            var fixture = new Settings { DefaultAction = "proxy", Language = "ru" };
            UiTheme.Apply("ru", "auto", window);
            typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, fixture);
            var updateSummary = typeof(MainWindow).GetMethod("UpdateProfileSummary", BindingFlags.NonPublic | BindingFlags.Instance)!;
            updateSummary.Invoke(window, null);
            Assert(((TextBlock)window.FindName("ProfileSummary")).Text.Contains("Добавьте"), "Explain the missing profile.");
            var profile = ProxyProfile.Create("Test proxy");
            profile.Proxy["host"] = "proxy.example.com";
            profile.Data["countryCode"] = "DE";
            fixture.Profiles.Add(profile);
            fixture.ActiveProfileId = profile.Id;
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [fixture]);
            Assert(((TextBlock)window.FindName("ProfileSummary")).Text.Contains("proxy.example.com:443"), "Show the selected proxy endpoint.");
            var settingsField = typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var applySettings = typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var originalSettings = PortableConfiguration.Copy((Settings)settingsField.GetValue(window)!);
            var pendingSettings = PortableConfiguration.Copy(originalSettings);
            var pendingSsh = ProxyProfile.Create("SSH KZ"); pendingSsh.Proxy["type"] = "SSH";
            pendingSsh.Proxy["host"] = "ssh.example.com"; pendingSsh.Proxy["port"] = 22;
            pendingSsh.Proxy["username"] = "test"; pendingSsh.Proxy["password"] = "secret";
            pendingSettings.Profiles.Add(pendingSsh); pendingSettings.ActiveProfileId = pendingSsh.Id;
            var runningSettings = PortableConfiguration.Copy(originalSettings);
            runningSettings.ActiveProfile!.Data["name"] = "Running H3 profile";
            runningSettings.ActiveProfile.Proxy["type"] = "MASQUE";
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            timer.Stop();
            {
                var fakeEngine = fixtureEngine;
                Wait(window, testConnection.ConnectAsync(runningSettings));
                try
                {
                    applySettings.Invoke(window, [pendingSettings]);
                    Layout(window);
                    string summary = ((TextBlock)window.FindName("ProfileSummary")).Text;
                    Assert(summary.Contains("Running H3 profile") && summary.Contains("MASQUE") && summary.Contains("HTTP/3") && !summary.Contains("SSH KZ"), "A pending SSH selection cannot replace the running profile name or mix SSH with the old H3 transport.");
                    Assert(((Button)window.FindName("ConnectButton")).IsEnabled && ((Button)window.FindName("ReconnectButton")).IsEnabled, "Accept-new enables SSH reconnect without a manually pasted server key.");
                    fakeEngine.AppliedSettings = pendingSettings;
                    fakeEngine.Http3Selected = false;
                    updateSummary.Invoke(window, null);
                    Assert(((TextBlock)window.FindName("ProfileSummary")).Text.Contains("SSH KZ") && !((TextBlock)window.FindName("ProfileSummary")).Text.Contains("HTTP/3"), "After reconnect the summary switches to the applied SSH profile and drops H3.");
                    Wait(window, testConnection.DisconnectAsync());
                    string key = "ssh-ed25519 " + Convert.ToBase64String(Convert.FromHexString("0000000B7373682D6564323535313900000020" + new string('0', 64)));
                    var update = new SshHostKeyUpdate(pendingSsh.Id, pendingSsh.Name, pendingSsh.Host, pendingSsh.Port, "", SshConfiguration.Fingerprint(key), key);
                    Wait(window, (Task)typeof(MainWindow).GetMethod("SaveSshHostKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [update])!);
                    Layout(window);
                    Assert(saved[^1].ActiveProfile!.Proxy["trustedHostKey"]!.ToString() == update.Fingerprint, "Accept-new persists the first SSH fingerprint through the normal settings store.");
                    var keyDialog = new SshHostKeyWindow(update);
                    keyDialog.Show();
                    Layout(keyDialog);
                    var keyButtons = Descendants(keyDialog).OfType<Button>().ToArray();
                    Assert(keyButtons.Single(button => button.IsDefault).IsCancel, "Changed SSH keys default to cancelling the connection.");
                    Assert(Descendants(keyDialog).OfType<TextBox>().Count(box => box.IsReadOnly) == 2, "Both fingerprints can be selected and copied.");
                    keyDialog.Close();
                }
                finally { Wait(window, testConnection.DisconnectAsync()); applySettings.Invoke(window, [originalSettings]); timer.Start(); }
            }
            ((CheckBox)window.FindName("AutoConnectBox")).IsChecked = false;
            var rules = (ObservableCollection<RoutingRule>)typeof(MainWindow).GetField("rules", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            rules.Clear();
            rules.Add(new RoutingRule { Source = "folder", Path = @"C:\Apps\Browser", Destination = "*.example.com" });
            window.Show();
            Layout(window);
            var commit = typeof(MainWindow).GetMethod("CommitSettingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var beforeSlowSave = ((Settings)settingsField.GetValue(window)!).Copy();
            object profileSource = ((ComboBox)window.FindName("ProfilePicker")).ItemsSource;
            object ruleSource = ((DataGrid)window.FindName("RulesList")).ItemsSource;
            slowSave = true;
            var slowSnapshot = beforeSlowSave.Copy(); slowSnapshot.AutoConnect = !slowSnapshot.AutoConnect;
            var slowCommit = (Task)commit.Invoke(window, [slowSnapshot])!;
            Wait(window, saveStarted.Task);
            bool pulse = false;
            Wait(window, window.Dispatcher.InvokeAsync(() => pulse = true, DispatcherPriority.Input).Task);
            Assert(pulse && !slowCommit.IsCompleted, "Dispatcher processes input while persistence is blocked on disk.");
            var nextSnapshot = slowSnapshot.Copy(); nextSnapshot.ShowAllConnections = true;
            var nextCommit = (Task)commit.Invoke(window, [nextSnapshot])!;
            var lastSnapshot = nextSnapshot.Copy(); lastSnapshot.AutoConnect = false;
            var lastCommit = (Task)commit.Invoke(window, [lastSnapshot])!;
            releaseSave.Set();
            Wait(window, Task.WhenAll(slowCommit, nextCommit, lastCommit)); Layout(window);
            Assert(saved[^1].ShowAllConnections && !saved[^1].AutoConnect, "Rapid UI changes save their latest combined state.");
            Assert(ReferenceEquals(profileSource, ((ComboBox)window.FindName("ProfilePicker")).ItemsSource) && ReferenceEquals(ruleSource, ((DataGrid)window.FindName("RulesList")).ItemsSource), "Preference changes preserve profile and rule views.");
            Wait(window, (Task)commit.Invoke(window, [beforeSlowSave])!); Layout(window);

            timer.Stop();
            Wait(window, testConnection.ConnectAsync((Settings)settingsField.GetValue(window)!));
            var refresh = typeof(MainWindow).GetMethod("RefreshConnectionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Wait(window, (Task)refresh.Invoke(window, null)!);
            int polls = fixtureEngine.Polls;
            Assert(polls > 0 && fixtureEngine.PollThread != Environment.CurrentManagedThreadId, "Connection decoding runs outside Dispatcher.");
            ((TabControl)window.FindName("Sections")).SelectedIndex = 3;
            Wait(window, (Task)refresh.Invoke(window, null)!);
            Assert(fixtureEngine.Polls == polls, "An inactive connections page does not poll the core.");
            ((TabControl)window.FindName("Sections")).SelectedIndex = 0;
            window.Hide();
            Wait(window, (Task)refresh.Invoke(window, null)!);
            Assert(fixtureEngine.Polls == polls, "A hidden tray window does not poll the core.");
            window.Show();
            Wait(window, testConnection.DisconnectAsync()); timer.Start();
            saved.Clear();
            var autoConnect = (CheckBox)window.FindName("AutoConnectBox");
            autoConnect.IsChecked = true;
            autoConnect.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            Assert(saved.Count == 1 && saved[^1].AutoConnect, "A setting change is saved immediately.");
            ((ComboBox)window.FindName("DefaultActionBox")).SelectedIndex = 1;
            Layout(window);
            Assert(saved.Count == 2 && saved[^1].DefaultAction == "system", "A selection change is saved immediately.");
            rules.Add(new RoutingRule { Destination = "example.net", Action = "block" });
            Layout(window);
            Assert(saved[^1].Rules.Count == 2, "Adding a rule saves without a Save button.");
            rules.Move(1, 0);
            Layout(window);
            Assert(saved[^1].Rules[0].Destination == "example.net", "Reordering rules saves immediately.");
            rules.RemoveAt(0);
            Layout(window);
            Assert(saved[^1].Rules.Count == 1, "Deleting a rule saves immediately.");
            Layout(window);
            int previousSaves = saved.Count;
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [saved[^1]]);
            Layout(window);
            Assert(saved.Count == previousSaves, "Loading settings must not write them back.");
            rejectSave = true;
            autoConnect.IsChecked = false;
            autoConnect.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            Assert(autoConnect.IsChecked == true, "A failed save restores the persisted value.");
            rejectSave = false;
            ((TextBlock)window.FindName("MessageText")).Text = "";
            var sections = (TabControl)window.FindName("Sections");
            var connect = (Button)window.FindName("ConnectButton");
            string output = Path.GetFullPath(AppIdentity.IsDev ? "artifacts/ui-checks-dev" : "artifacts/ui-checks");
            Directory.CreateDirectory(output);

#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                window.ThemeMode = theme;
                sections.SelectedIndex = 0;
                Layout(window);
                foreach (TabItem tab in sections.Items)
                {
                    var presenter = Descendants(tab).OfType<ContentPresenter>().First(item => item.Content?.ToString() == tab.Header.ToString());
                    var center = presenter.TransformToAncestor(tab).Transform(new Point(presenter.ActualWidth / 2, 0));
                    Assert(Math.Abs(center.X - tab.ActualWidth / 2) < 1.1, "Tab headers must be centered.");
                }
                Assert(Descendants((ComboBox)window.FindName("ProfilePicker")).OfType<TextBlock>().Any(text => text.Text.Contains("HTTPS")), "Profile picker displays the transport type.");
                Assert(connect.ActualHeight is >= 30 and <= 40, "Connect button must stay compact.");
                Assert(connect.ActualWidth is >= 120 and <= 180, "Connect button must not stretch.");
                Assert(Math.Abs(connect.ActualWidth - ((Button)window.FindName("ReconnectButton")).ActualWidth) < 0.1, "Connect and reconnect have identical widths.");
                Assert(connect.Style == window.FindResource("AccentButtonStyle"), "Use the native Fluent accent button.");
                Assert(connect.FontFamily.Source.Contains("Segoe UI"), "Controls use the Windows UI font.");
                Assert(Descendants(window).OfType<Image>().All(image => image.Source is null || image.Source.ToString()!.Contains("/Assets/Flags/")), "Only country flags appear in the content; no duplicate header logo.");
                Assert(!Descendants(window).OfType<Button>().Any(button => button.Content as string == "Выход"), "Exit belongs in the menu.");
                var headers = sections.Items.Cast<TabItem>().ToArray();
                Assert(headers.Length == 6, "Six navigation sections.");
                for (int i = 0; i < headers.Length; i++)
                {
                    sections.SelectedIndex = i;
                    Layout(window);
                    var indicator = (Border)headers[i].Template.FindName("SelectionIndicator", headers[i]);
                    Capture((Visual)window.Content, Path.Combine(output, $"{theme}-{i}.png"));
                    Assert(indicator.Visibility == Visibility.Visible && indicator.ActualHeight is >= 2 and <= 4, $"Selected section needs a visible indicator at the current DPI: {theme}/{i}, {indicator.Visibility}, height={indicator.ActualHeight}.");
                    Assert(headers[i].FontWeight == FontWeights.Normal, "Navigation text remains regular; the indicator marks selection.");
                }
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Layout(window);
                foreach (TabItem tab in sections.Items)
                {
                    var presenter = Descendants(tab).OfType<ContentPresenter>().First(item => item.Content?.ToString() == tab.Header.ToString());
                    var center = presenter.TransformToAncestor(tab).Transform(new Point(presenter.ActualWidth / 2, 0));
                    Assert(Math.Abs(center.X - tab.ActualWidth / 2) < 1.1, "Tab headers must be centered.");
                }
                Assert(Descendants((ComboBox)window.FindName("ProfilePicker")).OfType<TextBlock>().Any(text => text.Text.Contains("HTTPS")), "Profile picker displays the transport type.");
                Assert(connect.ActualHeight <= 40, "Compact button at the minimum window size.");
                Capture((Visual)window.Content, Path.Combine(output, $"{theme}-minimum.png"));
                window.Width = 1100;
                window.Height = 740;
            }
#pragma warning restore WPF0001
            Assert(!Descendants(window).OfType<Button>().Any(button => button.Content as string == "Сохранить"), "Main settings do not have Save buttons.");
            sections.SelectedIndex = 1;
            var profileList = (ListBox)window.FindName("ProfilesList");
            var previousSource = profileList.ItemsSource;
            profileList.ItemsSource = Enumerable.Range(0, 100).Select(index =>
            {
                var item = profile.Copy();
                item.Data["id"] = "scroll-" + index;
                item.Data["name"] = "Profile " + index;
                item.Data["color"] = index % ProfileAppearance.Colors.Count;
                item.Data["countryCode"] = new[] { "DE", "US", "JP", "GB" }[index % 4];
                return item;
            }).ToArray();
            Layout(window);
            var scroll = Descendants(profileList).OfType<ScrollViewer>().First();
            Assert(VirtualizingPanel.GetScrollUnit(profileList) == ScrollUnit.Pixel && scroll.ExtentHeight > 1000, "Profile scrolling uses pixels rather than whole cards.");
            scroll.LineDown();
            Layout(window);
            Assert(scroll.VerticalOffset is > 0 and <= 32, "A scroll line must move a small number of pixels.");
            scroll.ScrollToVerticalOffset(0); Layout(window);
            double expectedWheel = WindowInput.WheelDistance(-120, SystemParameters.WheelScrollLines,
                scroll.FontSize * scroll.FontFamily.LineSpacing, scroll.ViewportHeight);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Layout(window);
            Assert(Math.Abs(scroll.VerticalOffset - expectedWheel) < 1.1, "Mouse wheel honors Windows lines using text line height.");
            Assert(WindowInput.WheelDistance(-120, 0, 20, 400) == 0 && WindowInput.WheelDistance(-120, -1, 20, 400) == 400,
                "Zero-line and page-scroll system settings.");
            Capture((Visual)window.Content, Path.Combine(output, "profiles-scroll.png"));
            profileList.ItemsSource = previousSource;
            var autostart = (CheckBox)window.FindName("AutostartBox");
            if (AppIdentity.IsDev) Assert(!autostart.IsEnabled, "DEV cannot register a competing startup entry.");
            else
            {
            autostart.IsChecked = true; autostart.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            Assert(autostartEnabled && saved[^1].LaunchAtSignIn == true, "Autostart applies immediately through the OS adapter.");
            autostart.IsChecked = false; autostart.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            rejectAutostart = true;
            autostart.IsChecked = true; autostart.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            Assert(!autostartEnabled && autostart.IsChecked == false && saved[^1].LaunchAtSignIn == false, "Failed autostart reverts both UI and stored preference.");
            rejectAutostart = false;
            }
            ((ComboBox)window.FindName("LogSizeBox")).Text = "25";
            typeof(MainWindow).GetMethod("SaveLogSize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Layout(window);
            Assert(saved[^1].LogSizeMb == 25, "Log size autosaves.");
            Assert(((CheckBox)window.FindName("ShowAllConnectionsBox")).IsChecked == false, "Default-mode connections are hidden initially.");
            var showAll = (CheckBox)window.FindName("ShowAllConnectionsBox");
            showAll.IsChecked = true;
            showAll.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
            Assert(saved[^1].ShowAllConnections, "Show all connections saves immediately.");
            typeof(MainWindow).GetMethod("ApplySettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [PortableConfiguration.Copy(saved[^1])]);
            Assert(showAll.IsChecked == true, "Show all connections restores from saved settings.");
            var language = (ComboBox)window.FindName("LanguageBox");
            language.SelectedIndex = 0; Layout(window);
            Layout(window);
            Assert(saved[^1].Language == "en" && ((TabItem)sections.Items[3]).Header as string == "Settings", "English resources update in an open window and persist.");
            Assert(ProfileAppearance.Country("DE").Name == "Germany", "Country labels follow the selected application language.");
            Assert(((Button)window.FindName("ConnectButton")).Content as string == "Connect", "Runtime button labels switch to English.");
            var fingerprints = (ComboBox)window.FindName("TlsFingerprintBox");
            fingerprints.SelectedIndex = 1;
            Layout(window);
            Assert(saved[^1].TlsFingerprint == "chrome", "TCP fingerprint autosaves.");
            foreach (var gridName in new[] { "ConnectionsGrid", "RulesList", "SpeedResultsGrid" })
            {
                var headers = ((DataGrid)window.FindName(gridName)).Columns.Select(column => (string)column.Header).ToArray();
                Assert(headers[0] == (gridName == "SpeedResultsGrid" ? "Metric" : "Application") && headers.All(header => !System.Text.RegularExpressions.Regex.IsMatch(header, "[А-Яа-яЁё]")),
                    gridName + " column headers switch to English, including hidden tabs.");
            }
            var trayMenu = (ContextMenu)typeof(MainWindow).GetField("trayMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(trayMenu.PlacementTarget == window && ((MenuItem)trayMenu.Items[0]).Header as string == "Open " + AppIdentity.DisplayName, "Tray uses the application's WPF menu and language.");
            typeof(MainWindow).GetMethod("UpdateSpeedResults", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                [new SpeedResult("Without profile", Status: "Waiting"), new SpeedResult("Through profile", Status: "Error")]);
            Assert(((DataGrid)window.FindName("SpeedResultsGrid")).Items.Cast<SpeedMetricRow>().Last() is { Metric: "Status", Direct: "Waiting", Profile: "Error" }, "Speed results use English display labels.");
            var preferenceThemeBox = (ComboBox)window.FindName("ThemeBox");
            preferenceThemeBox.SelectedIndex = 2; Layout(window);
#pragma warning disable WPF0001
            Layout(window);
            Assert(saved[^1].Theme == "dark" && window.ThemeMode == ThemeMode.Dark, "Theme changes apply and save immediately.");
#pragma warning restore WPF0001
            window.Hide();
            trayMenu.IsOpen = true; Layout(window); trayMenu.UpdateLayout();
            Assert(trayMenu.IsVisible && trayMenu.Background is SolidColorBrush { Color.R: < 128 }, "Tray menu opens in the dark app theme while the main window is hidden.");
            Capture(trayMenu, Path.Combine(output, "tray-dark.png"));
            trayMenu.IsOpen = false;
            window.Show();
            preferenceThemeBox.SelectedIndex = 1; Layout(window);
            trayMenu.IsOpen = true; Layout(window); trayMenu.UpdateLayout();
            Assert(trayMenu.Background is SolidColorBrush { Color.R: > 128 }, "Tray menu switches to the light app theme.");
            Capture(trayMenu, Path.Combine(output, "tray-light.png"));
            trayMenu.IsOpen = false;
            preferenceThemeBox.SelectedIndex = 0;
            language.SelectedIndex = 1; Layout(window);
            Layout(window);
            Assert(saved[^1].Language == "ru" && ProfileAppearance.Country("DE").Name == "Германия", "Russian remains an explicit saved translation.");
            foreach (var gridName in new[] { "ConnectionsGrid", "RulesList", "SpeedResultsGrid" })
                Assert(((DataGrid)window.FindName(gridName)).Columns[0].Header as string == (gridName == "SpeedResultsGrid" ? "Параметр" : "Приложение"), "Table headers switch back to Russian.");
            Assert(((MenuItem)trayMenu.Items[0]).Header as string == Ui.T("Open MegaProxy").Replace("MegaProxy", AppIdentity.DisplayName), "Tray menu follows language changes.");
            var groupedRows = new[]
            {
                new ConnectionRow("z-system", "example.com:443", "TCP", "System", "0 B", "0 B", 2),
                new ConnectionRow("a-profile", "example.com:443", "TCP", "Test proxy", "0 B", "0 B", 0),
                new ConnectionRow("a-blocked", "example.com:443", "TCP", "Blocked", "0 B", "0 B", 1),
                new ConnectionRow("z-profile", "example.com:443", "TCP", "Test proxy", "0 B", "0 B", 0)
            };
            var updateConnections = typeof(MainWindow).GetMethod("UpdateConnections", BindingFlags.NonPublic | BindingFlags.Instance)!;
            updateConnections.Invoke(window, [groupedRows]);
            var connectionView = (System.Windows.Data.ListCollectionView)((DataGrid)window.FindName("ConnectionsGrid")).ItemsSource;
            connectionView.SortDescriptions.Add(new System.ComponentModel.SortDescription("Process", System.ComponentModel.ListSortDirection.Descending));
            Assert(connectionView.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().Select(group => (int)group.Name).SequenceEqual(new[] { 0, 1, 2 }), "Connection groups retain proxy, blocked, system order regardless of row sorting.");
            Assert(((System.Windows.Data.CollectionViewGroup)connectionView.Groups[0]).Items.Cast<ConnectionRow>().Select(row => row.Process).SequenceEqual(new[] { "z-profile", "a-profile" }), "Column sorting applies within each connection group.");
            updateConnections.Invoke(window, [groupedRows]);
            connectionView = (System.Windows.Data.ListCollectionView)((DataGrid)window.FindName("ConnectionsGrid")).ItemsSource;
            Assert(connectionView.SortDescriptions.Count == 1 && connectionView.SortDescriptions[0].Direction == System.ComponentModel.ListSortDirection.Descending, "Refreshing connections preserves the chosen row sorting.");
            sections.SelectedIndex = 0; Layout(window);
            var liveGrid = (DataGrid)window.FindName("ConnectionsGrid");
            liveGrid.SelectedItem = groupedRows[1];
            liveGrid.Focus();
            var changedRows = groupedRows.Select(row => row.Process == "a-profile"
                ? new ConnectionRow(row.Process, row.Destination, row.Network, row.Route, "2 KiB", "3 KiB", 0, 2048, 3072)
                : row).Append(new ConnectionRow("new-process", "new.example:443", "TCP", "System", "0 B", "0 B")).ToArray();
            updateConnections.Invoke(window, [changedRows]);
            Layout(window);
            Assert(ReferenceEquals(liveGrid.ItemsSource, connectionView) && ReferenceEquals(liveGrid.SelectedItem, groupedRows[1]), "Polling keeps the collection and selected connection alive.");
            Assert(groupedRows[1].UploadedBytes == 2048 && connectionView.Cast<ConnectionRow>().Any(row => row.Process == "new-process"), "A selected table keeps receiving new connections and counter updates.");
            updateConnections.Invoke(window, [changedRows.Where(row => row.Process != "new-process").ToArray()]);
            Assert(!connectionView.Cast<ConnectionRow>().Any(row => row.Process == "new-process"), "Ended connections are removed from the existing view.");
            connectionView.SortDescriptions.Clear();
            connectionView.SortDescriptions.Add(new System.ComponentModel.SortDescription("UploadedBytes", System.ComponentModel.ListSortDirection.Descending));
            Layout(window);
            Assert(((System.Windows.Data.CollectionViewGroup)connectionView.Groups![0]).Items.Cast<ConnectionRow>().First().Process == "a-profile", "Traffic sorting uses numeric counters.");
            updateConnections.Invoke(window, [groupedRows.Select(row => row.Process == "z-profile"
                ? new ConnectionRow(row.Process, row.Destination, row.Network, row.Route, "4 KiB", "0 B", 0, 4096) : row).ToArray()]);
            Layout(window);
            Assert(((System.Windows.Data.CollectionViewGroup)connectionView.Groups![0]).Items.Cast<ConnectionRow>().First().Process == "z-profile", "Live counter updates reorder rows within their existing route group.");
            connectionView.SortDescriptions.Clear();
            connectionView.SortDescriptions.Add(new System.ComponentModel.SortDescription("Process", System.ComponentModel.ListSortDirection.Descending));
            Capture((Visual)window.Content, Path.Combine(output, "connection-groups.png"));
            ((DataGrid)window.FindName("SpeedResultsGrid")).Visibility = Visibility.Collapsed;
            var connectionsContent = (DependencyObject)((TabItem)sections.Items[0]).Content;
            Assert(!Descendants(connectionsContent).Contains((Button)window.FindName("TestConnectionButton")), "The connections page has no diagnostic button.");
            Assert(window.FindName("RttText") is null, "No synthetic RTT is shown in live connection statistics.");
            var diagnosticTab = (TabItem)window.FindName("DiagnosticsTab");
            Assert(((TabItem)sections.Items[sections.Items.IndexOf(diagnosticTab) - 1]).Header as string == "Настройки", "Diagnostics is immediately to the right of settings.");
            Assert(!Descendants((DependencyObject)((TabItem)sections.Items[3]).Content).OfType<TextBlock>().Any(text => text.Text.StartsWith("Закрытие окна")), "Settings have no long explanatory footer.");
            Assert(((StackPanel)window.FindName("SpeedPanel")).Visibility == Visibility.Collapsed, "Speed measurements stay locked until diagnostics succeed.");
            fixtureEngine.Start = token => Task.Delay(System.Threading.Timeout.Infinite, token);
            var pendingConnection = testConnection.ConnectAsync((Settings)settingsField.GetValue(window)!);
            int blockedEntries = diagnosticEntries;
            sections.SelectedItem = diagnosticTab; Layout(window);
            Assert(diagnosticEntries == blockedEntries && testConnection.State == ConnectionState.Connecting, "Entering diagnostics during startup does not launch an automatic check.");
            Assert(((Button)window.FindName("ConnectButton")).IsEnabled && ((Button)window.FindName("ConnectButton")).Content as string == Ui.T("Cancel connection"), "A pending connection exposes an enabled cancel action.");
            testConnection.Cancel();
            var cancelWatch = System.Diagnostics.Stopwatch.StartNew();
            while (!pendingConnection.IsCompleted && cancelWatch.Elapsed < TimeSpan.FromSeconds(5)) Layout(window);
            Assert(pendingConnection.IsCompleted, "The UI can finish startup cancellation without closing the application.");
            try { pendingConnection.GetAwaiter().GetResult(); throw new Exception("Startup was not cancelled."); }
            catch (OperationCanceledException) { }
            fixtureEngine.Start = null;
            sections.SelectedIndex = 0; Layout(window);
            sections.SelectedItem = diagnosticTab; Layout(window);
            Assert(Descendants((DependencyObject)diagnosticTab.Content).Contains((Button)window.FindName("TestConnectionButton")), "The dedicated diagnostics tab owns its controls.");
            typeof(MainWindow).GetMethod("AppendDiagnosticEntry", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Synthetic diagnostic step"]);
            sections.SelectedIndex = 0;
            int entries = diagnosticEntries;
            sections.SelectedItem = diagnosticTab; Layout(window);
            Assert(diagnosticEntries == entries + 1, "Entering the tab starts diagnostics automatically.");
            sections.SelectedItem = diagnosticTab; Layout(window);
            Assert(diagnosticEntries == entries + 1, "Remaining on the selected tab does not restart diagnostics.");
            Assert(((TextBox)window.FindName("TestLogText")).Text.Contains("Synthetic diagnostic step"), "Diagnostic steps are selectable and retained across navigation.");
            typeof(MainWindow).GetMethod("AppendDiagnosticEntry", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Test stopped."]);
            Layout(window);
            Assert(((TextBox)window.FindName("TestLogText")).Text.Contains("Test stopped."), "Diagnostic log entries stay English under the Russian UI.");
            Capture((Visual)window.Content, Path.Combine(output, "diagnostics-log.png"));
            typeof(MainWindow).GetMethod("CompleteDiagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                [new DiagnosticResult("203.0.113.7", "DE", 32, ""), fixture]);
            Assert(((TextBlock)window.FindName("DiagnosticHttp3Text")).Visibility == Visibility.Collapsed, "Hide HTTP/3 results when the applied transport has no confirmed UDP support.");
            typeof(MainWindow).GetMethod("CompleteDiagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                [new DiagnosticResult("203.0.113.7", "DE", 32, "HTTP/3 · example.test: confirmed"), fixture]);
            Assert(((TextBlock)window.FindName("DiagnosticHttp3Text")).Visibility == Visibility.Visible, "Show HTTP/3 results for UDP-capable transports.");
            typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false]);
            Assert(((TextBox)window.FindName("DiagnosticIpText")).Text == "203.0.113.7" && ((TextBox)window.FindName("DiagnosticIpText")).IsReadOnly,
                "Result includes a selectable external IP.");
            var countryPanel = (StackPanel)window.FindName("DiagnosticCountryPanel");
            Assert(((ProfileCountry)countryPanel.DataContext).Name == "Германия" && Descendants(countryPanel).OfType<Image>().Any(image => image.Source?.ToString()?.EndsWith("/DE.png") == true),
                "Diagnostics display the country flag and the country name in the UI language.");
            Assert(((StackPanel)window.FindName("SpeedPanel")).Visibility == Visibility.Visible && ((Button)window.FindName("SpeedTestButton")).IsEnabled,
                "Successful diagnostics reveal and enable the speed test.");
            Assert(((DataGrid)window.FindName("SpeedResultsGrid")).Visibility == Visibility.Collapsed, "No empty results table before a speed measurement.");
            ((ComboBox)window.FindName("SpeedSizeBox")).SelectedIndex = 1;
            Assert(((TextBlock)window.FindName("SpeedTrafficText")).Text.Contains("100"), "Traffic disclosure updates for the selected size.");
            Assert(diagnosticEntries == entries + 1, "Changing speed options must not restart diagnostics.");
            Layout(window); Capture((Visual)window.Content, Path.Combine(output, "diagnostics-success.png"));
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            foreach (bool minimum in new[] { false, true })
            {
                window.ThemeMode = theme;
                window.Width = minimum ? window.MinWidth : 1100;
                window.Height = minimum ? window.MinHeight : 740;
                var scrollViewer = (ScrollViewer)window.FindName("DiagnosticsScroll");
                scrollViewer.ScrollToTop(); Layout(window);
                var resultCard = (Border)window.FindName("DiagnosticResultCard");
                var speedCard = (Border)window.FindName("DiagnosticSpeedCard");
                Assert(Math.Abs(resultCard.ActualWidth - speedCard.ActualWidth) < 1, "Diagnostic cards share one layout grid.");
                Assert(resultCard.Padding == new Thickness(16) && speedCard.Padding == new Thickness(16), "Cards use consistent 16 DIP padding.");
                Assert(!((Expander)window.FindName("TestLogExpander")).IsExpanded, "Technical log is collapsed by default.");
                Capture((Visual)window.Content, Path.Combine(output, $"diagnostics-{theme}-{(minimum ? "minimum" : "normal")}.png"));
                ((Button)window.FindName("SpeedTestButton")).BringIntoView(); Layout(window);
                var speedButton = (Button)window.FindName("SpeedTestButton");
                var location = speedButton.TransformToAncestor(speedCard).Transform(new Point(0, 0));
                Assert(location.X >= 16 && location.X + speedButton.ActualWidth <= speedCard.ActualWidth - 15, "Speed action fits inside its card at every window size.");
                Assert(((ComboBox)window.FindName("SpeedSizeBox")).ActualWidth >= 140 && ((ComboBox)window.FindName("SpeedProtocolBox")).ActualWidth >= 140, "Settings fields remain readable.");
                Capture((Visual)window.Content, Path.Combine(output, $"diagnostics-speed-{theme}-{(minimum ? "minimum" : "normal")}.png"));
            }
            window.Width = 1100; window.Height = 740;
#pragma warning restore WPF0001
            typeof(MainWindow).GetMethod("UpdateSpeedResults", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window,
                [new SpeedResult("direct", Country: "DE"), new SpeedResult("proxy", Country: "JP")]);
            var speedRows = ((DataGrid)window.FindName("SpeedResultsGrid")).Items.Cast<SpeedMetricRow>().ToArray();
            Assert(speedRows[1].Direct == "Германия (DE)" && speedRows[1].DirectFlag!.EndsWith("/DE.png") && speedRows[1].ProfileFlag!.EndsWith("/JP.png"), "Speed comparison shows localized countries and flags.");
            ((Expander)window.FindName("TestLogExpander")).IsExpanded = true;
            CheckLogScrolling(window, (TextBox)window.FindName("TestLogText"));
            sections.SelectedIndex = sections.Items.Count - 1; Layout(window);
            CheckLogScrolling(window, (TextBox)window.FindName("LogText"));
            Assert(!Descendants(window).OfType<Button>().Any(button => button.Content as string == "Применить автозапуск"), "No separate autostart save button.");
            string fullPath = @"C:\Users\Example\AppData\Local\Programs\VeryLongApplicationDirectory\nested\client.exe";
            string shortened = PathTextBox.Shorten(fullPath, value => value.Length <= 35);
            Assert(shortened.StartsWith("C:\\") && shortened.Contains('…') && shortened.EndsWith("client.exe"), "Long paths preserve prefix and exe.");
            sections.SelectedIndex = 0;
            var grid = (DataGrid)window.FindName("ConnectionsGrid");
            grid.ItemsSource = new[] { new ConnectionRow(fullPath, "example.com:443", "TCP", "Test proxy", "1 КиБ", "2 КиБ") };
            Layout(window);
            var pathBox = Descendants(grid).OfType<PathTextBox>().First();
            Assert(pathBox.ToolTip as string == fullPath && pathBox.FontWeight == FontWeights.Normal, "Full paths available by tooltip with regular text.");
            pathBox.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, pathBox) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Assert(pathBox.Text == fullPath && pathBox.SelectedText == fullPath, "Focusing a path exposes selectable full text.");
            Assert(((DataGridTemplateColumn)grid.Columns[0]).ClipboardContentBinding is System.Windows.Data.Binding { Path.Path: "Process" }, "Row copy uses the original full path.");
            Assert(grid.FontWeight == FontWeights.Normal, "Connection table uses regular text.");
            typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true]);
            Assert(((Grid)window.FindName("ProfilePanel")).IsEnabled && ((Grid)window.FindName("RulesPanel")).IsEnabled && ((ComboBox)window.FindName("ProfilePicker")).IsEnabled,
                "Connection transitions do not lock profile and rule editing.");
            typeof(MainWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false]);
            Capture((Visual)window.Content, Path.Combine(output, "connections-paths.png"));
            grid.ItemsSource = null;
            sections.SelectedIndex = 2; Layout(window);
            var editableRule = new RoutingRule { Source = "folder", Path = @"C:\Apps\Editable", Destination = "*.before.example", Action = "proxy", ProfileId = profile.Id };
            rules.Insert(1, editableRule);
            rules.Add(new RoutingRule { Destination = "tail.example", Action = "block" });
            var ruleGrid = (DataGrid)window.FindName("RulesList");
            Layout(window);
            ruleGrid.SelectedIndex = 1;
            Layout(window);
            int countBeforeEdit = rules.Count, savesBeforeEdit = saved.Count;
            var editRuleButton = Descendants((DependencyObject)((TabItem)sections.Items[2]).Content).OfType<Button>().Single(button => button.Content as string == "Изменить");
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var edit = window.OwnedWindows.OfType<RuleWindow>().Single();
                Assert(RuleField<ComboBox>(edit, "source").SelectedIndex == 2 && RuleField<TextBox>(edit, "path").Text == editableRule.Path,
                    "Rule editor preloads source and recursive folder path.");
                Assert(RuleField<TextBox>(edit, "destination").Text == "*.before.example",
                    "Rule editor preloads destination and named profile.");
                RuleField<TextBox>(edit, "destination").Text = "*.after.example";
                Assert(editableRule.Destination == "*.before.example", "Editing must not mutate the original rule before confirmation.");
                Descendants(edit).OfType<Button>().Single(button => button.Content as string == "Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            editRuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert(saved.Count == savesBeforeEdit + 1 && rules.Count == countBeforeEdit && rules[1].Destination == "*.after.example" && rules[1].ProfileId is null,
                "Saving replaces the same rule in place and persists immediately.");
            Assert(ruleGrid.SelectedIndex == 1 && rules[^1].Destination == "tail.example", "Editing preserves rule priority and selection.");
            Layout(window);
            int savedAfterEdit = saved.Count;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var edit = window.OwnedWindows.OfType<RuleWindow>().Single();
                RuleField<TextBox>(edit, "destination").Text = "cancelled.example";
                edit.Close();
            }));
            editRuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window);
            Assert(saved.Count == savedAfterEdit && rules[1].Destination == "*.after.example", "Cancel leaves the existing rule and storage unchanged.");
            rejectSave = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var edit = window.OwnedWindows.OfType<RuleWindow>().Single();
                RuleField<TextBox>(edit, "destination").Text = "failed-save.example";
                Descendants(edit).OfType<Button>().Single(button => button.Content as string == "Сохранить").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            editRuleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
            Layout(window);
            Assert(saved.Count == savedAfterEdit && rules[1].Destination == "*.after.example", "A failed edit save restores the persisted rule.");
            rejectSave = false;
            foreach (var input in new[]
            {
                new RoutingRule { Source = "exe", Path = @"C:\Apps\client.exe", Action = "system" },
                new RoutingRule { Action = "block" }
            })
            {
                var edit = new RuleWindow(input);
                Assert(RuleField<ComboBox>(edit, "source").SelectedIndex == (input.Source == "exe" ? 1 : 0) &&
                    RuleField<ComboBox>(edit, "action").SelectedIndex == (input.Action == "system" ? 1 : 2),
                    "EXE, all-app, system and block rules preload without selecting a proxy.");
                edit.Close();
            }
            var dialog = new RuleWindow { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
            dialog.Show();
            Layout(dialog);
            Assert(Descendants(dialog).OfType<Button>().Any(button => button.IsCancel), "Rule dialog supports Escape/cancel.");
            Capture((Visual)dialog.Content, Path.Combine(output, "rule-dialog.png"));
            dialog.Close();
            Assert(testConnection.Engine is null, "UI check must never start TUN.");
            var editorProfile = profile.Copy();
            editorProfile.Data["browser"] = new System.Text.Json.Nodes.JsonObject { ["futureField"] = "retain" };
            var editor = new ProfileWindow(editorProfile) { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
            editor.Show();
            Layout(editor);
            var countryPicker = (ComboBox)editor.FindName("CountryBox");
            Assert(VirtualizingPanel.GetScrollUnit(countryPicker) == ScrollUnit.Pixel && countryPicker.ItemsPanel.LoadContent() is VirtualizingStackPanel,
                "Dropdown lists use the same pixel scrolling panel and global Windows wheel handler.");
            Assert(((TextBox)editor.FindName("AuthProbeHostBox")).Text == "" && ((StackPanel)editor.FindName("AuthProbeHostPanel")).Visibility == Visibility.Visible,
                "HTTPS profiles expose the optional knock host.");
            Capture((Visual)editor.Content, Path.Combine(output, "profile-editor.png"));
            editor.Close();
            foreach (var type in new[] { "HTTPS", "HTTPS_JUMP", "MASQUE", "SSH", "SSH_JUMP" })
            {
                var input = editorProfile.Copy();
                input.Proxy["type"] = type;
                if (type == "SSH")
                {
                    string key = "ssh-ed25519 " + Convert.ToBase64String(Convert.FromHexString("0000000B7373682D6564323535313900000020" + new string('0', 64)));
                    input.Proxy["username"] = "test"; input.Proxy["password"] = "secret";
                    input.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(key);
                    input.Data["windows"] = new System.Text.Json.Nodes.JsonObject { ["sshHostKey"] = key };
                }
                if (type == "SSH_JUMP") input.Proxy["jump"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "jump.example", ["port"] = 22 };
                if (type == "HTTPS_JUMP") input.Proxy["jump"] = new System.Text.Json.Nodes.JsonObject
                { ["host"] = "jump.example", ["port"] = 443, ["sameAuthentication"] = false, ["username"] = "jump-user", ["password"] = "jump-secret", ["futureJump"] = true };
                var edit = new ProfileWindow(input) { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
                edit.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    ((TextBox)edit.FindName("NameBox")).Text = "Renamed profile";
                    ((ComboBox)edit.FindName("CountryBox")).SelectedItem = ProfileAppearance.Countries.First(country => country.Code == "JP");
                    ((ComboBox)edit.FindName("ColorBox")).SelectedIndex = 0;
                    if (type == "HTTPS") ((CheckBox)edit.FindName("PreferHttp3Box")).IsChecked = true;
                    Descendants(edit).OfType<Button>().First(button => button.Content as string == "Сохранить")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                Assert(edit.ShowDialog() == true && edit.Profile?.Name == "Renamed profile", "The profile editor saves the user's changes.");
                Assert(edit.Profile!.Data["browser"]!["futureField"]!.ToString() == "retain", "Editing a profile preserves foreign platform fields.");
                Assert(edit.Profile.CountryCode == "JP" && edit.Profile.ColorIndex == 0, "Selecting a country and automatic color uses the flag's dominant color.");
                if (type == "SSH_JUMP") Assert(System.Text.Json.Nodes.JsonNode.DeepEquals(input.Proxy, edit.Profile.Proxy), "Unsupported Jump settings stay unchanged when renamed.");
                if (type == "SSH") Assert(edit.Profile.Supported && SshConfiguration.Build(edit.Profile, "test")["host_key"]![0]!.ToString() == SshConfiguration.PublicKey(input), "SSH editing preserves the pinned server key and creates a verified SSH outbound.");
                if (type == "HTTPS") Assert(edit.Profile.PreferHttp3, "The profile editor saves the shared preferHttp3 option.");
                if (type == "MASQUE") Assert(edit.Profile.Supported && edit.Profile.UsesHttp3 && !edit.Profile.PreferHttp3, "MASQUE is editable and requires HTTP/3 without an optional fallback switch.");
                if (type == "HTTPS_JUMP") Assert(edit.Profile.Supported && edit.Profile.Proxy["jump"]!["username"]!.ToString() == "jump-user" && edit.Profile.Proxy["jump"]!["futureJump"]!.GetValue<bool>(), "Jump editing preserves separate credentials and unknown server options.");
            }
            var exportDialog = new ExportWindow { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowInTaskbar = false };
            Assert(!exportDialog.IncludePasswords && !exportDialog.IncludePrivateKeys, "Credentials must be opt-in for export.");
            exportDialog.Show();
            Layout(exportDialog);
            Capture((Visual)exportDialog.Content, Path.Combine(output, "export-dialog.png"));
            exportDialog.Close();
            var protectedData = SettingsStore.Protect("synthetic-profile-password-and-other-platform-secret");
            Assert(!protectedData.Contains("synthetic") && SettingsStore.Unprotect(protectedData) == "synthetic-profile-password-and-other-platform-secret", "DPAPI protects imported data.");
            Assert(SendMessage(new WindowInteropHelper(window).Handle, 0x8487, IntPtr.Zero, IntPtr.Zero).ToInt64() == 0x487,
                "A hidden window accepts the graceful restart command.");
            Console.WriteLine("UI checks passed: compact controls, navigation, light/dark themes, minimum size, dialog. TUN not started.");
        }
        finally
        {
            var quit = (Task)typeof(MainWindow).GetMethod("QuitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Wait(window, quit);
        }
    }

    private static void Wait(Window window, Task task, bool propagate = true)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            System.Threading.Thread.Sleep(1);
        }
        Assert(task.IsCompleted, "Background UI work must finish without blocking Dispatcher.");
        if (propagate) task.GetAwaiter().GetResult();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Layout(Window window)
    {
        if (window is MainWindow)
        {
            var flush = (Task)typeof(MainWindow).GetMethod("FlushSettingsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
            Wait(window, flush, propagate: false);
            var diagnostics = (Task)typeof(MainWindow).GetField("diagnosticTextUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Wait(window, diagnostics);
        }
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task CheckEngineAsync()
    {
        var directory = Path.GetFullPath("artifacts/engine-check");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, "result.txt");
        File.Delete(report);
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", Path.GetFullPath(".tools/sing-box/sing-box.exe"));
        var before = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(item => item.Name == "MegaProxy").Select(item => item.Id).ToArray();
        using var engine = new Engine(directory);
        var messages = new System.Collections.Generic.List<string>();
        engine.Log += message => { lock (messages) messages.Add(message); };
        try
        {
            await engine.StartAsync(new Settings(), testOnly: true);
            Assert(engine.Running, "Real sing-box runs a diagnostic-only instance.");
            string signature = engine.AppliedSignature!;
            var updated = PortableConfiguration.Copy(engine.AppliedSettings!);
            updated.AutoConnect = !updated.AutoConnect;
            Assert(Configuration.Signature(updated) == signature, "UI-only settings do not require reconnect.");
            updated.BypassLan = !updated.BypassLan;
            Assert(Configuration.Signature(updated) != signature && engine.AppliedSignature == signature, "Connection edits require reconnect while the running snapshot remains unchanged.");
            var snapshot = await engine.GetConnectionsAsync(false);
            Assert(snapshot.Rows.Count == 0, "The real authenticated API supplies a connection snapshot.");
            using var probeClient = engine.CreateProbeClient(false);
            using var probeTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(75));
            var details = await NetworkDiagnostics.CheckAsync(probeClient, false, _ => { }, probeTimeout.Token);
            Assert(NetworkDiagnostics.ParseIp(details.ExitIp) is not null && details.Country is not null, "Real diagnostics obtain exit IP and country without logging identity.");
            var speed = await NetworkDiagnostics.MeasureAsync(probeClient, "Без профиля", 10, "h2", false, _ => { }, _ => { }, probeTimeout.Token);
            Assert(speed.Download.Contains("Мбит/с") && speed.Upload.Contains("Мбит/с"), "Cloudflare accepts the streaming 10 MB download and upload.");
            var after = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(item => item.Name == "MegaProxy").Select(item => item.Id).ToArray();
            Assert(before.SequenceEqual(after), "Diagnostics must not create a TUN interface.");
        }
        finally { await engine.StopAsync(); }
        Assert(!Directory.GetFiles(directory, "*.json").Any(), "Temporary diagnostic configurations are removed.");
        Assert(messages.Count > 0, "Engine lifecycle and diagnostics reach the log sink.");
        File.WriteAllText(report, "Real sing-box API, HTTPS RTT, exit IP/country, 10 MB download/upload, snapshot and cleanup passed; TUN unchanged. Identity not recorded.");
        Console.WriteLine("Real sing-box diagnostic API, HTTPS RTT, graceful stop and cleanup passed; TUN unchanged.");
    }

    private static async Task CheckProbeRouteAsync()
    {
        string directory = Path.GetFullPath("artifacts/engine-route-check");
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "result.txt"));
        File.Delete(Path.Combine(directory, "failure.txt"));
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", Path.GetFullPath(".tools/sing-box/sing-box.exe"));
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var rejector = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        rejector.Start();
        int contacts = 0;
        var rejection = Task.Run(async () =>
        {
            using var peer = await rejector.AcceptTcpClientAsync(timeout.Token);
            System.Threading.Interlocked.Increment(ref contacts);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
            string first = await reader.ReadLineAsync(timeout.Token) ?? "";
            Assert(first.StartsWith("CONNECT example.com:443"), "Diagnostics must reach the configured proxy.");
            int count = first.Length;
            bool preemptiveAuth = false;
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
            { count += line.Length; Assert(count < 16384, "Bounded proxy headers."); preemptiveAuth |= line.Equals("Proxy-Authorization: Basic OnNlY3JldA==", StringComparison.OrdinalIgnoreCase); }
            Assert(preemptiveAuth, "Masked proxies receive authentication on the first CONNECT, including an empty username.");
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"), timeout.Token);
        });
        var profile = ProxyProfile.Create("Synthetic rejecting proxy");
        profile.Proxy["type"] = "HTTP"; profile.Proxy["host"] = "127.0.0.1";
        profile.Proxy["username"] = ""; profile.Proxy["password"] = "secret";
        profile.Proxy["port"] = ((System.Net.IPEndPoint)rejector.LocalEndpoint).Port;
        using var core = new Engine(directory);
        try
        {
            await core.StartAsync(new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" }, true, timeout.Token);
            var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory, "*.json").Single()))!;
            int port = config["inbounds"]![0]!["listen_port"]!.GetValue<int>();
            using var unauthenticated = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            { UseProxy = true, Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{port}") });
            try
            {
                using var response = await unauthenticated.GetAsync("http://example.com/", timeout.Token);
                Assert(response.StatusCode == System.Net.HttpStatusCode.ProxyAuthenticationRequired, "Unauthenticated requests must be rejected.");
            }
            catch (System.Net.Http.HttpRequestException) { }
            Assert(contacts == 0, "Unauthenticated diagnostic requests must not reach the configured proxy.");
            using var client = core.CreateProbeClient(true);
            bool failed = false;
            try { await NetworkDiagnostics.CheckAsync(client, true, _ => { }, timeout.Token); }
            catch (System.Net.Http.HttpRequestException) { failed = true; }
            Assert(failed, "Proxy failure must not fall back to a direct successful test.");
            await rejection;
        }
        finally { timeout.Cancel(); rejector.Stop(); await core.StopAsync(); }
        File.WriteAllText(Path.Combine(directory, "result.txt"), "Authenticated diagnostic ingress, selected proxy routing and no direct fallback passed; TUN not started.");
    }

    private static void Capture(Visual visual, string path)
    {
        var element = (FrameworkElement)visual;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            context.DrawRectangle((Brush)element.FindResource("SolidBackgroundFillColorBaseBrush"), null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void CheckLogScrolling(Window window, TextBox box)
    {
        box.BringIntoView(); Layout(window);
        LogAutoScroll.Replace(box, string.Join(Environment.NewLine, Enumerable.Range(0, 150).Select(i => "Log entry " + i)));
        Layout(window);
        Assert(LogAutoScroll.IsFollowing(box) && box.VerticalOffset > 0, "Logs initially follow the last entry.");
        box.ScrollToHome(); Layout(window);
        Assert(!LogAutoScroll.IsFollowing(box), "Scrolling up disables follow mode.");
        LogAutoScroll.Append(box, Environment.NewLine + "new entry"); Layout(window);
        Assert(!LogAutoScroll.IsFollowing(box) && box.VerticalOffset < 1, "New entries preserve the user's reading position.");
        LogAutoScroll.Replace(box, box.Text + Environment.NewLine + "replacement"); Layout(window);
        Assert(!LogAutoScroll.IsFollowing(box) && box.VerticalOffset < 1, "Refreshing a disk log also preserves the reading position.");
        box.ScrollToEnd(); Layout(window);
        Assert(LogAutoScroll.IsFollowing(box), "Returning to the bottom re-enables follow mode.");
        LogAutoScroll.Append(box, Environment.NewLine + "last entry"); Layout(window);
        Assert(LogAutoScroll.IsFollowing(box) && box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 1, "Resumed logs continue to follow new entries.");
        LogAutoScroll.Replace(box, box.Text.Replace("entry", "other")); Layout(window);
        Assert(LogAutoScroll.IsFollowing(box) && box.VerticalOffset >= box.ExtentHeight - box.ViewportHeight - 1, "Replacing a bounded log tail without changing its height preserves follow mode.");
        foreach (var (previous, next) in new[] { ("", "new"), ("abcabc", "abc"), ("aaaa", "aaab"), ("old\nretained\n", "retained\nnew\n"), ("x", ""), ("🇯🇵\n", "🇯🇵\nnext") })
        {
            box.Text = previous;
            LogAutoScroll.Replace(box, next);
            Assert(box.Text == next, "Incremental log replacement handles rolling tails, repeated prefixes, empty text and Unicode.");
        }
        Layout(window);
    }

    private static T RuleField<T>(RuleWindow window, string name) =>
        (T)typeof(RuleWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
