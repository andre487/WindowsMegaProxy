using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Data;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WindowsMegaProxy;

public sealed record SpeedMetricRow(string Metric, string Direct, string Profile, string? DirectFlag = null, string? ProfileFlag = null);

public partial class MainWindow : Window
{
    private Settings settings;
    private readonly ObservableCollection<RoutingRule> rules;
    private readonly Forms.NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly Icon lightTrayIcon;
    private readonly Icon connectedIcon;
    private readonly ContextMenu trayMenu;
    private readonly MenuItem trayOpen, trayConnect, trayExit;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Engine? engine;
    private Engine? diagnosticEngine;
    private readonly bool startInTray;
    private readonly Action<Settings> persistSettings;
    private readonly DiagnosticLog log;
    private readonly Func<bool, Task> setAutostart;
    private readonly Func<Task<bool>> getAutostart;
    private bool testing, applyingAutostart, logDirty;
    private bool speedRunning, diagnosticSucceeded;
    private readonly Func<Task> diagnosticsOnEnter;
    private Settings? checkedSettings;
    private DiagnosticResult? lastDiagnosticResult;
    private (SpeedResult Direct, SpeedResult Profile)? lastSpeedResults;
    private CancellationTokenSource? diagnosticCancellation;
    private long previousUpload, previousDownload;
    private DateTimeOffset previousTrafficTime;
    private bool busy, polling, quitting, updatingUi, restartRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(RestartMessage);
    }

    private IntPtr RestartMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x8487) return IntPtr.Zero;
        handled = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (restartRequested || quitting) return;
            restartRequested = true;
            while (busy) await Task.Delay(100);
            await QuitAsync();
        });
        return new IntPtr(0x487);
    }

    public MainWindow(bool startInTray, Action<Settings>? persistSettings = null, DiagnosticLog? diagnosticLog = null,
        Func<bool, Task>? setAutostart = null, Func<Task<bool>>? getAutostart = null, Func<Task>? diagnosticsOnEnter = null)
    {
        this.persistSettings = persistSettings ?? SettingsStore.Save;
        this.setAutostart = setAutostart ?? Autostart.SetAsync;
        this.getAutostart = getAutostart ?? Autostart.IsEnabledAsync;
        this.diagnosticsOnEnter = diagnosticsOnEnter ?? (() => RunConnectionTestAsync());
        log = diagnosticLog ?? new DiagnosticLog(Path.Combine(SettingsStore.DirectoryPath, "logs"));
        settings = SettingsStore.Load();
        Ui.Apply(settings.Language, settings.Theme, this);
        InitializeComponent();
        this.startInTray = startInTray;
        log.SetLimit(settings.LogSizeMb);
        log.Changed += LogChanged;
        WriteLog("Application started.");
        rules = new ObservableCollection<RoutingRule>();
        ApplySettings(settings);
        RulesList.ItemsSource = rules;
        trayIcon = LoadIcon("tray.ico");
        lightTrayIcon = LoadIcon("tray-light.ico");
        connectedIcon = LoadIcon("app.ico");
        trayOpen = new MenuItem { Header = Ui.T("Open MegaProxy") };
        trayOpen.Click += (_, _) => ShowWindow();
        trayConnect = new MenuItem { Header = Ui.T("Connect"), IsEnabled = CanConnect() };
        trayConnect.Click += async (_, _) => await ToggleAsync();
        trayExit = new MenuItem { Header = Ui.T("Exit") };
        trayExit.Click += async (_, _) => await QuitAsync();
        trayMenu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        trayMenu.Items.Add(trayOpen);
        trayMenu.Items.Add(trayConnect);
        trayMenu.Items.Add(new Separator());
        trayMenu.Items.Add(trayExit);
        trayMenu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(trayMenu) is HwndSource source) SetForegroundWindow(source.Handle);
            trayMenu.Focus();
        };
        tray = new Forms.NotifyIcon { Icon = DisconnectedIcon(), Text = Ui.T("MegaProxy — disconnected"), Visible = true };
        tray.MouseUp += (_, args) => { if (args.Button == Forms.MouseButtons.Right) trayMenu.IsOpen = true; };
        tray.DoubleClick += (_, _) => ShowWindow();
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        timer.Tick += async (_, _) =>
        {
            await RefreshConnectionsAsync();
            RefreshLog();
        };
        timer.Start();
        DefaultActionBox.SelectionChanged += SettingsChanged;
        BypassLanBox.Click += SettingsChanged;
        AutoConnectBox.Click += SettingsChanged;
        AutostartBox.Click += AutostartChanged;
        LogSizeBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => SaveLogSize());
        LanguageBox.SelectionChanged += PreferencesChanged;
        ThemeBox.SelectionChanged += PreferencesChanged;
        TlsFingerprintBox.SelectionChanged += PreferencesChanged;
        rules.CollectionChanged += SettingsChanged;
        Sections.SelectionChanged += SectionsChanged;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (startInTray) Hide();
        try
        {
            bool enabled = await getAutostart();
            AutostartBox.IsChecked = enabled;
            if (settings.AutoConnect) await ToggleAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag) ?? throw new InvalidOperationException(Ui.T("Unknown settings value."));
    }

    private static Icon LoadIcon(string name)
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/WindowsMegaProxy;component/Assets/" + name)).Stream;
        using var icon = new Icon(resource);
        return (Icon)icon.Clone();
    }

    private void SaveSettings()
    {
        CommitSettings(CaptureSettings());
    }

    private Settings CaptureSettings()
    {
        var updated = PortableConfiguration.Copy(settings);
        updated.DefaultAction = (string)((ComboBoxItem)DefaultActionBox.SelectedItem).Tag;
        updated.BypassLan = BypassLanBox.IsChecked == true;
        updated.AutoConnect = AutoConnectBox.IsChecked == true;
        updated.ShowAllConnections = ShowAllConnectionsBox.IsChecked == true;
        updated.Language = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
        updated.Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag;
        updated.TlsFingerprint = (string)((ComboBoxItem)TlsFingerprintBox.SelectedItem).Tag;
        if (!applyingAutostart) updated.LaunchAtSignIn = AutostartBox.IsChecked == true;
        updated.Rules = rules.ToList();
        PortableConfiguration.ValidateSettings(updated);
        return updated;
    }

    private void CommitSettings(Settings updated)
    {
        ProfileAppearance.AssignMissingColors(updated.Profiles);
        persistSettings(updated);
        ApplySettings(updated);
        log.SetLimit(updated.LogSizeMb);
        WriteLog("Settings saved.");
        SetBusy(busy);
    }

    private void ApplySettings(Settings updated)
    {
        updatingUi = true;
        try
        {
            ProfileAppearance.AssignMissingColors(updated.Profiles);
            settings = updated;
            Ui.Apply(updated.Language, updated.Theme, this);
            SelectTag(LanguageBox, updated.Language);
            SelectTag(ThemeBox, updated.Theme);
            SelectTag(TlsFingerprintBox, updated.TlsFingerprint);
            ShowAllConnectionsBox.IsChecked = updated.ShowAllConnections;
            ProfilePicker.ItemsSource = settings.Profiles;
            ProfilePicker.SelectedItem = settings.ActiveProfile;
            ProfilesList.ItemsSource = settings.Profiles;
            ProfilesList.SelectedItem = settings.ActiveProfile;
            SelectTag(DefaultActionBox, settings.DefaultAction);
            BypassLanBox.IsChecked = settings.BypassLan;
            AutoConnectBox.IsChecked = settings.AutoConnect;
            AutostartBox.IsChecked = settings.LaunchAtSignIn == true;
            LogSizeBox.Text = settings.LogSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rules.Clear();
            foreach (var rule in settings.Rules)
            {
                rules.Add(rule);
            }
        }
        finally { updatingUi = false; }
        UpdateProfileSummary();
        if (tray is not null)
        {
            bool connected = engine?.Running == true;
            StatusText.Text = Ui.T(connected ? "Connected" : "Disconnected");
            ConnectButton.Content = trayConnect.Header = Ui.T(connected ? "Disconnect" : "Connect");
            trayOpen.Header = Ui.T("Open MegaProxy");
            trayExit.Header = Ui.T("Exit");
            tray.Text = Ui.T(connected ? "MegaProxy — TUN connected" : "MegaProxy — disconnected");
            SetBusy(busy);
        }
        SpeedSizeChanged(this, null!);
        if (diagnosticSucceeded && lastDiagnosticResult is not null && checkedSettings is not null)
            CompleteDiagnostics(lastDiagnosticResult, checkedSettings);
        if (lastSpeedResults is { } results) UpdateSpeedResults(results.Direct, results.Profile);
    }

    private void UpdateProfileSummary()
    {
        bool connected = engine?.Running == true;
        var profile = connected ? engine!.AppliedSettings?.ActiveProfile : settings.ActiveProfile;
        ProfileSummary.Text = profile is null ? Ui.T("Add or import a profile on the Profiles tab")
            : profile.Supported ? profile.Name + " · " + profile.Description : profile.Availability;
        if (connected && profile?.UsesHttp3 == true)
            ProfileSummary.Text += engine!.UsedHttp3Fallback ? " · " + Ui.T("HTTP/3 unavailable; using stable HTTPS/TCP") : engine.Http3Selected ? " · HTTP/3" : "";
        string? error = ConnectionError();
        ConnectButton.IsEnabled = connected || error is null;
        ConnectButton.ToolTip = connected ? null : error;
        if (!connected && profile is not null && error is not null) ProfileSummary.Text += " · " + error;
    }

    private bool CanConnect() => ConnectionError() is null;

    private Engine CreateEngine() => new()
    {
        ConfirmSshHostKeyChange = async update => await Dispatcher.InvokeAsync(() => new SshHostKeyWindow(update) { Owner = this }.ShowDialog() == true),
        SaveSshHostKey = SaveSshHostKey
    };

    private void SaveSshHostKey(SshHostKeyUpdate update)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SaveSshHostKey(update)); return; }
        var updated = PortableConfiguration.Copy(settings);
        var profile = updated.Profiles.FirstOrDefault(item => item.Id == update.ProfileId);
        if (profile is null || profile.Type != "SSH" || profile.Port != update.Port ||
            !string.Equals(profile.Host.Trim('[', ']'), update.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
            (profile.Proxy["trustedHostKey"]?.GetValue<string>() ?? "") != update.PreviousFingerprint)
            throw new InvalidOperationException(Ui.T("The SSH profile changed during key verification. Connect again."));
        if (profile.Data["windows"] is not System.Text.Json.Nodes.JsonObject) profile.Data["windows"] = new System.Text.Json.Nodes.JsonObject();
        profile.Data["windows"]!["sshHostKey"] = update.PublicKey;
        profile.Proxy["trustedHostKey"] = update.Fingerprint;
        CommitSettings(updated);
        WriteLog("Saved the verified SSH server key.");
    }

    private string? ConnectionError()
    {
        try { Configuration.Build(settings, 9999, "validation", allowUnresolvedSsh: true); return null; }
        catch (ArgumentException error) { return error.Message; }
        catch (System.IO.InvalidDataException error) { return error.Message; }
    }

    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingUi || quitting || ProfilePicker.SelectedItem is not ProxyProfile profile) return;
        try
        {
            var updated = CaptureSettings();
            updated.ActiveProfileId = profile.Id;
            CommitSettings(updated);
        }
        catch (Exception error) { ApplySettings(settings); ShowError(error); }
    }

    private void AddProfileClick(object sender, RoutedEventArgs e) => EditProfile(ProxyProfile.Create(), isNew: true);
    private void EditProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is ProxyProfile profile) EditProfile(profile, isNew: false);
    }
    private void CloneProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProxyProfile profile) return;
        var copy = profile.Copy();
        copy.Data["id"] = Guid.NewGuid().ToString();
        copy.Data["name"] = profile.Name + Ui.T(" — copy");
        EditProfile(copy, isNew: true);
    }
    private void EditProfile(ProxyProfile profile, bool isNew)
    {
        var dialog = new ProfileWindow(profile, settings.Profiles) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        try
        {
            var updated = CaptureSettings();
            if (isNew) updated.Profiles.Add(dialog.Profile);
            else updated.Profiles[updated.Profiles.FindIndex(item => item.Id == profile.Id)] = dialog.Profile;
            if (updated.ActiveProfile is null) updated.ActiveProfileId = dialog.Profile.Id;
            CommitSettings(updated);
            ProfilesList.SelectedItem = settings.Profiles.First(item => item.Id == dialog.Profile.Id);
        }
        catch (Exception error) { ShowError(error); }
    }
    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProxyProfile profile) return;
        try
        {
            if (rules.Any(rule => rule.ProfileId == profile.Id)) throw new InvalidOperationException(Ui.T("The profile is used by rules. Change those rules first."));
            if (ContainsProfileReference(settings.SharedOptions, profile.Id)) throw new InvalidOperationException(Ui.T("The profile is used by another client's settings. Remove those assignments in that client first."));
            if (MessageBox.Show(this, Ui.F("Delete profile “{0}”?", profile.Name), Ui.T("Profiles"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var updated = CaptureSettings();
            updated.Profiles.RemoveAll(item => item.Id == profile.Id);
            if (updated.ActiveProfile is null) updated.ActiveProfileId = updated.Profiles.FirstOrDefault()?.Id;
            CommitSettings(updated);
        }
        catch (Exception error) { ShowError(error); }
    }

    // shortcut: matching foreign strings conservatively count as references; add schema-aware pruning when needed.
    private static bool ContainsProfileReference(System.Text.Json.Nodes.JsonNode? node, string id) => node switch
    {
        System.Text.Json.Nodes.JsonValue value => value.TryGetValue<string>(out var text) && text == id,
        System.Text.Json.Nodes.JsonObject obj => obj.Any(item => ContainsProfileReference(item.Value, id)),
        System.Text.Json.Nodes.JsonArray array => array.Any(item => ContainsProfileReference(item, id)),
        _ => false
    };

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Filter = "MegaProxy JSON|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var updated = PortableConfiguration.Import(PortableConfiguration.ReadFile(dialog.FileName), CaptureSettings());
            CommitSettings(updated);
            int unsupported = settings.Profiles.Count(profile => !profile.Supported);
            MessageText.Text = Ui.F("Imported {0} profiles; unsupported connection types: {1}. Windows startup was not changed.", settings.Profiles.Count, unsupported);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new ExportWindow { Owner = this };
            if (options.ShowDialog() != true) return;
            var document = PortableConfiguration.Export(CaptureSettings(), options.IncludePasswords, options.IncludePrivateKeys);
            var dialog = new SaveFileDialog { FileName = "MegaProxy.json", DefaultExt = ".json", Filter = "MegaProxy JSON|*.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            SettingsStore.WriteAtomic(dialog.FileName, document.ToJsonString(Configuration.JsonOptions));
            MessageText.Text = Ui.T("MegaProxy.json exported.");
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ConnectClick(object sender, RoutedEventArgs e) => await ToggleAsync();

    private async Task ToggleAsync()
    {
        if (busy || quitting || (testing && engine?.Running != true)) return;
        busy = true; SetBusy(true);
        try
        {
            if (engine?.Running == true)
            {
                diagnosticCancellation?.Cancel();
                StatusText.Text = Ui.T("Disconnecting…");
                await engine.StopAsync(); engine.Dispose(); engine = null;
                SetConnected(false);
            }
            else
            {
                if (settings.ActiveProfile is { Type: "HTTPS" or "HTTPS_JUMP" or "MASQUE" } selected &&
                    (selected.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() == true || selected.Type == "HTTPS_JUMP" && selected.Proxy["jump"]?["allowInvalidProxyCertificate"]?.GetValue<bool>() == true) &&
                    MessageBox.Show(this, Ui.T("Proxy certificate verification is disabled in this profile. Connect with this exception?"), Ui.T("Proxy certificate"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                SaveSettings();
                if (settings.ActiveProfile is { Type: "SSH" } ssh && ssh.Proxy["acceptAnyHostKey"]?.GetValue<bool>() == true &&
                    MessageBox.Show(this, Ui.T("SSH server identity verification is disabled in this profile. Connect with this exception?"), "SSH", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                if (engine is not null) { await engine.StopAsync(); engine.Dispose(); }
                engine = CreateEngine();
                engine.Log += WriteLog;
                var current = engine;
                engine.Exited += () => Dispatcher.BeginInvoke(async () =>
                {
                    if (engine != current || busy || quitting) return;
                    SetConnected(false);
                    MessageText.Text = Ui.T("The core exited unexpectedly. Connections are no longer protected by TUN.");
                    tray.ShowBalloonTip(5000, "MegaProxy", Ui.T("The core exited. TUN is disconnected."), Forms.ToolTipIcon.Warning);
                    await current.StopAsync();
                    SetBusy(false);
                });
                StatusText.Text = Ui.T("Connecting…");
                await engine.StartAsync(settings);
                SetConnected(true); timer.Start();
                previousTrafficTime = default;
                MessageText.Text = "";
                await RefreshConnectionsAsync();
            }
        }
        catch (Exception error) { SetConnected(false); ShowError(error); }
        finally { busy = false; SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        ConnectButton.IsEnabled = !value && (engine?.Running == true || (!testing && CanConnect()));
        trayConnect.IsEnabled = ConnectButton.IsEnabled;
        bool editable = !quitting;
        ProfilePanel.IsEnabled = editable; RulesPanel.IsEnabled = editable;
        ProfilePicker.IsEnabled = editable;
        TestConnectionButton.IsEnabled = !value && !testing && (engine?.Running == true || CanConnect());
        SpeedOptionsPanel.IsEnabled = !testing;
        SpeedTestButton.IsEnabled = !value && diagnosticSucceeded && (!testing || speedRunning);
        ReconnectButton.IsEnabled = !value && !testing && engine?.Running == true && CanConnect();
        bool changed = false;
        if (engine?.Running == true)
        {
            try { changed = engine.AppliedSignature != Engine.Signature(settings); }
            catch (ArgumentException) { changed = true; }
        }
        ReconnectButton.Content = changed ? Ui.T("Reconnect •") : Ui.T("Reconnect");
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton,
            changed ? Ui.T("Reconnect and apply changes") : Ui.T("Reconnect"));
        ReconnectButton.ToolTip = changed ? Ui.T("Apply profile and rule changes") : Ui.T("Reconnect using current settings");
    }

    private void SetConnected(bool value)
    {
        StatusText.Text = value ? Ui.T("Connected") : Ui.T("Disconnected");
        ConnectButton.Content = trayConnect.Header = value ? Ui.T("Disconnect") : Ui.T("Connect");
        tray.Text = value ? Ui.T("MegaProxy — TUN connected") : Ui.T("MegaProxy — disconnected");
        tray.Icon = value ? connectedIcon : DisconnectedIcon();
        UpdateProfileSummary();
        WriteLog(value ? "TUN connected." : "TUN disconnected.");
        if (!value)
        {
            ConnectionsGrid.ItemsSource = null; ConnectionsHint.Text = Ui.T("TUN disconnected.");
            TrafficText.Text = "↑ —  ↓ —";
        }
    }

    private async Task RefreshConnectionsAsync()
    {
        if (polling || engine?.Running != true) return;
        polling = true;
        var current = engine;
        try
        {
            var snapshot = await current.GetConnectionsAsync(ShowAllConnectionsBox.IsChecked == true);
            if (current != engine || !current.Running) return;
            if (!ConnectionsGrid.IsKeyboardFocusWithin) UpdateConnections(snapshot.Rows);
            ConnectionsHint.Text = Ui.F("Shown: {0} of {1}.", snapshot.Rows.Count, snapshot.Total) +
                (ConnectionsGrid.IsKeyboardFocusWithin ? Ui.T(" List updates paused for copying.") : "");
            var now = DateTimeOffset.UtcNow;
            if (previousTrafficTime != default)
            {
                double seconds = (now - previousTrafficTime).TotalSeconds;
                TrafficText.Text = $"↑ {ConnectionSnapshot.Bytes(Math.Max(0, snapshot.Upload - previousUpload) / seconds)}/s   ↓ {ConnectionSnapshot.Bytes(Math.Max(0, snapshot.Download - previousDownload) / seconds)}/s";
            }
            previousTrafficTime = now; previousUpload = snapshot.Upload; previousDownload = snapshot.Download;
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or ObjectDisposedException or System.Text.Json.JsonException or IOException or InvalidOperationException or FormatException)
        { ConnectionsHint.Text = Ui.T("Statistics are temporarily unavailable."); }
        finally { polling = false; }
    }

    private void UpdateConnections(System.Collections.Generic.IReadOnlyList<ConnectionRow> rows)
    {
        var sorting = (ConnectionsGrid.ItemsSource as ListCollectionView)?.SortDescriptions.ToArray() ?? [];
        var view = new ListCollectionView(rows.ToList());
        ConnectionsGrid.ItemsSource = view;
        using (view.DeferRefresh())
        {
            var grouping = new PropertyGroupDescription(nameof(ConnectionRow.RouteGroup));
            grouping.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            view.GroupDescriptions.Add(grouping);
            foreach (var sort in sorting) view.SortDescriptions.Add(sort);
        }
        foreach (var column in ConnectionsGrid.Columns)
            column.SortDirection = sorting.FirstOrDefault(sort => sort.PropertyName == column.SortMemberPath) is { PropertyName.Length: > 0 } match ? match.Direction : null;
    }

    private void SettingsChanged(object? sender, EventArgs e)
    {
        if (updatingUi || quitting) return;
        try
        {
            var updated = CaptureSettings();
            persistSettings(updated);
            settings = updated;
            SetBusy(busy);
            WriteLog("Settings saved.");
        }
        catch (Exception error)
        {
            // CollectionChanged cannot restore the collection while its notification is being delivered.
            Dispatcher.BeginInvoke(() => { ApplySettings(settings); SetBusy(busy); ShowError(error); });
        }
    }

    private async void AutostartChanged(object sender, RoutedEventArgs e)
    {
        if (updatingUi || applyingAutostart) return;
        applyingAutostart = true;
        AutostartBox.IsEnabled = false;
        bool previous = settings.LaunchAtSignIn == true;
        try
        {
            bool requested = AutostartBox.IsChecked == true;
            previous = await getAutostart();
            try
            {
                await setAutostart(requested);
                var updated = CaptureSettings();
                updated.LaunchAtSignIn = requested;
                CommitSettings(updated);
            }
            catch
            {
                await setAutostart(previous);
                throw;
            }
            WriteLog("Startup preference updated.");
        }
        catch (Exception error)
        {
            updatingUi = true;
            AutostartBox.IsChecked = previous;
            updatingUi = false;
            ShowError(error);
        }
        finally { applyingAutostart = false; AutostartBox.IsEnabled = true; }
    }

    private void AddRuleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Rule is not null) rules.Add(dialog.Rule);
    }

    private void EditRuleClick(object sender, RoutedEventArgs e) => EditSelectedRule();

    private void EditSelectedRule()
    {
        if (RulesList.SelectedItem is not RoutingRule rule) return;
        var dialog = new RuleWindow(rule) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Rule is null) return;
        int index = rules.IndexOf(rule);
        if (index < 0) return;
        rules[index] = dialog.Rule;
        RulesList.SelectedIndex = index;
    }

    private void RulesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        for (var node = origin; node is not null && node != RulesList;)
        {
            if (node is TextBox) return;
            node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (ItemsControl.ContainerFromElement(RulesList, origin) is not DataGridRow row) return;
        RulesList.SelectedItem = row.Item;
        EditSelectedRule();
        e.Handled = true;
    }

    private void RulesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        EditSelectedRule();
        e.Handled = true;
    }

    private async void ShowAllConnectionsClick(object sender, RoutedEventArgs e)
    {
        SettingsChanged(sender, e);
        await RefreshConnectionsAsync();
    }

    private void PreferencesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingUi || quitting) return;
        try { CommitSettings(CaptureSettings()); }
        catch (Exception error) { ApplySettings(settings); ShowError(error); }
    }

    private async void ReconnectClick(object sender, RoutedEventArgs e)
    {
        if (engine?.Running != true || testing) return;
        await ToggleAsync();
        if (engine?.Running != true && !quitting) await ToggleAsync();
    }

    private async void TestConnectionClick(object sender, RoutedEventArgs e) => await RunConnectionTestAsync();

    private async Task RunConnectionTestAsync()
    {
        if (testing || quitting) return;
        testing = true;
        SetBusy(busy);
        diagnosticSucceeded = false;
        lastDiagnosticResult = null;
        lastSpeedResults = null;
        checkedSettings = null;
        diagnosticCancellation = new CancellationTokenSource();
        SpeedPanel.Visibility = Visibility.Collapsed;
        SpeedLockedText.Visibility = Visibility.Visible;
        DiagnosticCountryPanel.Visibility = Visibility.Collapsed;
        DiagnosticDetailsPanel.Visibility = Visibility.Collapsed;
        TestResultText.Visibility = Visibility.Visible;
        SpeedResultsGrid.ItemsSource = null;
        SpeedResultsGrid.Visibility = Visibility.Collapsed;
        TestStatusText.Text = Ui.T("Testing…");
        TestResultText.Text = engine?.Running == true ? Ui.T("Testing the applied connection.") : Ui.T("Testing the current profile without starting TUN.");
        LogAutoScroll.Replace(TestLogText, "");
        TestProgress.Visibility = Visibility.Visible;
        Engine? temporary = null;
        Engine? target = null;
        try
        {
            target = engine;
            if (target?.Running != true)
            {
                var snapshot = PortableConfiguration.Copy(settings);
                if (snapshot.ActiveProfile is { Supported: false } unsupported) throw new InvalidOperationException(unsupported.Availability);
                if (snapshot.ActiveProfile?.Supported == true) snapshot.DefaultAction = "proxy";
                diagnosticEngine = temporary = target = CreateEngine();
                target.Log += WriteLog;
                target.Log += AppendDiagnosticEntry;
                await target.StartAsync(snapshot, testOnly: true, diagnosticCancellation?.Token ?? CancellationToken.None);
            }
            else target.Log += AppendDiagnosticEntry;
            if (target.HasProbeProfile && !target.Http3Selected)
                await ProxyAuthenticationProbe.CheckAsync(target.AppliedSettings!.ActiveProfile,
                    message => { AppendDiagnosticEntry(message); WriteLog(message); }, diagnosticCancellation!.Token);
            using var probe = target.CreateProbeClient(target.HasProbeProfile);
            var details = await NetworkDiagnostics.CheckAsync(probe, target.HasProbeProfile,
                message => { AppendDiagnosticEntry(message); WriteLog(message); }, diagnosticCancellation!.Token);
            if (target.HasProbeProfile)
            {
                string http3 = await target.CheckHttp3Async(diagnosticCancellation!.Token);
                details = details with { Http3 = http3 };
                if (http3.Length > 0) { AppendDiagnosticEntry(http3); WriteLog(http3); }
            }
            if (!quitting && (temporary is not null || target == engine))
            {
                CompleteDiagnostics(details, target.AppliedSettings!);
                TestStatusText.Text += " · " + ConnectionSnapshot.RouteName(target.SelectedOutbound, target.AppliedSettings!);
                if (target.Http3Selected) TestStatusText.Text += " · HTTP/3";
                else if (target.UsedHttp3Fallback) TestStatusText.Text += " · " + Ui.T("HTTP/3 unavailable; using stable HTTPS/TCP");
            }
        }
        catch (OperationCanceledException) when (diagnosticCancellation?.IsCancellationRequested == true)
        {
            if (!quitting)
            {
                TestStatusText.Text = Ui.T("Test stopped");
                TestResultText.Text = Ui.T("The connection or test was stopped.");
                AppendDiagnosticEntry("Test stopped.");
            }
        }
        catch (Exception error)
        {
            if (!quitting)
            {
                TestStatusText.Text = Ui.T("Test failed");
                TestResultText.Text = error.Message;
                AppendDiagnosticEntry("Diagnostic failed: " + error.GetType().Name);
                ShowError(error);
            }
        }
        finally
        {
            try { if (temporary is not null) await temporary.StopAsync(); }
            catch (Exception error) { if (!quitting) ShowError(error); }
            finally
            {
                temporary?.Dispose(); diagnosticEngine = null;
                if (target is not null) target.Log -= AppendDiagnosticEntry;
                testing = false;
                diagnosticCancellation?.Dispose(); diagnosticCancellation = null;
                if (!quitting)
                {
                    TestProgress.Visibility = Visibility.Collapsed;
                    SetBusy(busy);
                }
            }
        }
    }

    private void CompleteDiagnostics(DiagnosticResult details, Settings snapshot)
    {
        lastDiagnosticResult = details;
        diagnosticSucceeded = true;
        checkedSettings = PortableConfiguration.Copy(snapshot);
        TestStatusText.Text = Ui.T("Test passed");
        TestResultText.Text = "";
        TestResultText.Visibility = Visibility.Collapsed;
        DiagnosticDetailsPanel.Visibility = Visibility.Visible;
        DiagnosticIpText.Text = details.ExitIp;
        DiagnosticLatencyText.Text = Ui.F("{0} ms", details.LatencyMs);
        DiagnosticHttp3Text.Text = string.Join(Environment.NewLine, details.Http3.Split(Environment.NewLine).Select(line =>
            Ui.T(line.Replace("HTTP/3 · ", "", StringComparison.Ordinal))
                .Replace(": unavailable", ": " + Ui.T("unavailable"), StringComparison.Ordinal)
                .Replace(": confirmed", ": " + Ui.T("confirmed"), StringComparison.Ordinal)));
        DiagnosticHttp3Text.Visibility = string.IsNullOrEmpty(details.Http3) ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticCountryPanel.Visibility = Visibility.Visible;
        DiagnosticCountryPanel.DataContext = ProfileAppearance.Country(details.Country ?? "");
        SpeedPanel.Visibility = Visibility.Visible;
        SpeedLockedText.Visibility = Visibility.Collapsed;
    }

    private async void SectionsChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLog();
        if (e.Source != Sections || !e.AddedItems.Contains(DiagnosticsTab) || !IsLoaded || updatingUi || quitting) return;
        if (testing) return;
        await diagnosticsOnEnter();
    }

    private void SpeedSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedSizeBox?.SelectedItem is ComboBoxItem { Tag: string size } && SpeedTrafficText is not null)
            SpeedTrafficText.Text = Ui.F("Cloudflare: {0} MB download and upload per route, up to {1} MB of traffic.", size, int.Parse(size) * 4);
    }

    private async void SpeedTestClick(object sender, RoutedEventArgs e)
    {
        if (speedRunning) { diagnosticCancellation?.Cancel(); return; }
        if (testing || !diagnosticSucceeded || checkedSettings is null) return;
        testing = speedRunning = true;
        diagnosticCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = diagnosticCancellation.Token;
        SpeedTestButton.Content = Ui.T("Stop speed test");
        SetBusy(busy);
        Engine? temporary = null;
        Engine? target = engine;
        SpeedResult direct = new("Without profile"), profile = new("Through profile");
        void Update(bool throughProfile, SpeedResult result)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Update(throughProfile, result)); return; }
            if (throughProfile) profile = result; else direct = result;
            UpdateSpeedResults(direct, profile);
        }
        try
        {
            if (target?.Running == true && target.AppliedSignature != Engine.Signature(checkedSettings))
                throw new InvalidOperationException(Ui.T("The connection changed. Run diagnostics again before testing speed."));
            if (target?.Running != true)
            {
                diagnosticEngine = temporary = target = CreateEngine();
                target.Log += WriteLog;
                await target.StartAsync(checkedSettings, testOnly: true, token);
            }
            int size = int.Parse((string)((ComboBoxItem)SpeedSizeBox.SelectedItem).Tag);
            string protocol = (string)((ComboBoxItem)SpeedProtocolBox.SelectedItem).Tag;
            Update(false, direct); Update(true, profile);
            foreach (bool throughProfile in new[] { false, true })
            {
                string name = throughProfile ? "Through profile" : "Without profile";
                if (throughProfile && !target.HasProbeProfile) { Update(true, profile with { Status = "No profile selected" }); continue; }
                try
                {
                    using var probe = target.CreateProbeClient(throughProfile);
                    await Task.Run(() => NetworkDiagnostics.MeasureAsync(probe, name, size, protocol, throughProfile,
                        result => Update(throughProfile, result), message => { AppendDiagnosticEntry(message); WriteLog(message); }, token), token);
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    Update(throughProfile, (throughProfile ? profile : direct) with { Status = "Error" });
                    AppendDiagnosticEntry(name + ": " + error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { AppendDiagnosticEntry("Speed test stopped."); Update(false, direct with { Status = "Stopped" }); Update(true, profile with { Status = "Stopped" }); }
        catch (Exception error) { AppendDiagnosticEntry("Diagnostic failed: " + error.GetType().Name); ShowError(error); }
        finally
        {
            try { if (temporary is not null) await temporary.StopAsync(); }
            catch (Exception error) { if (!quitting) ShowError(error); }
            finally
            {
                temporary?.Dispose(); diagnosticEngine = null;
                diagnosticCancellation.Dispose(); diagnosticCancellation = null;
                testing = speedRunning = false;
                if (!quitting) { SpeedTestButton.Content = Ui.T("Test speed"); SetBusy(busy); }
            }
        }
    }

    private void UpdateSpeedResults(SpeedResult direct, SpeedResult profile)
    {
        lastSpeedResults = (direct, profile);
        SpeedResultsGrid.Visibility = Visibility.Visible;
        var directCountry = NetworkDiagnostics.ParseCountry(direct.Country) is { } dc ? ProfileAppearance.Country(dc) : null;
        var profileCountry = NetworkDiagnostics.ParseCountry(profile.Country) is { } pc ? ProfileAppearance.Country(pc) : null;
        SpeedResultsGrid.ItemsSource = new[]
        {
            new SpeedMetricRow("IP", direct.Ip, profile.Ip),
            new SpeedMetricRow(Ui.T("Country"), directCountry?.Label ?? direct.Country, profileCountry?.Label ?? profile.Country, directCountry?.FlagUri, profileCountry?.FlagUri),
            new SpeedMetricRow(Ui.T("Protocol"), direct.Protocol, profile.Protocol),
            new SpeedMetricRow(Ui.T("Latency"), direct.Latency, profile.Latency),
            new SpeedMetricRow(Ui.T("Download"), direct.Download, profile.Download),
            new SpeedMetricRow(Ui.T("Upload"), direct.Upload, profile.Upload),
            new SpeedMetricRow(Ui.T("Status"), Ui.T(direct.Status), Ui.T(profile.Status))
        };
    }

    private void AppendDiagnosticEntry(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendDiagnosticEntry(message)); return; }
        if (quitting) return;
        LogAutoScroll.Append(TestLogText, $"{DateTime.Now:HH:mm:ss} {DiagnosticLog.Redact(message)}{Environment.NewLine}");
    }

    private void LogChanged() => logDirty = true;

    private void WriteLog(string message)
    {
        try { log.Write(message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (quitting) return;
            Dispatcher.BeginInvoke(() => { if (!quitting) MessageText.Text = Ui.T("Could not write the log to disk."); });
        }
    }

    private void RefreshLog()
    {
        if (!logDirty || !((TabItem)Sections.Items[^1]).IsSelected || LogText.SelectionLength > 0) return;
        try { LogAutoScroll.Replace(LogText, log.Read(tailOnly: true)); logDirty = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageText.Text = Ui.T("Could not read the log."); }
    }

    private void ExportLogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "MegaProxy.log", Filter = Ui.T("Text log|*.log"), AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { SettingsStore.WriteAtomic(dialog.FileName, log.Read()); }
        catch (Exception error) { ShowError(error); }
    }

    private void LogSizeChanged(object sender, KeyboardFocusChangedEventArgs e) => SaveLogSize();
    private void LogSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveLogSize(); e.Handled = true; }
    }

    private void SaveLogSize()
    {
        if (updatingUi || quitting || !IsLoaded) return;
        if (!int.TryParse(LogSizeBox.Text, out int size) || size is < 1 or > 100)
        {
            MessageText.Text = Ui.T("Enter a log size between 1 and 100 MiB.");
            LogSizeBox.Text = settings.LogSizeMb.ToString();
            return;
        }
        if (size == settings.LogSizeMb) return;
        try
        {
            var updated = CaptureSettings(); updated.LogSizeMb = size;
            CommitSettings(updated);
            WriteLog("Log size changed.");
        }
        catch (Exception error) { ShowError(error); }
    }
    private void DeleteRuleClick(object sender, RoutedEventArgs e) { if (RulesList.SelectedItem is RoutingRule rule) rules.Remove(rule); }
    private void MoveRuleUpClick(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDownClick(object sender, RoutedEventArgs e) => MoveRule(1);
    private void MoveRule(int offset)
    {
        int index = RulesList.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= rules.Count) return;
        rules.Move(index, index + offset); RulesList.SelectedIndex = index + offset;
    }
    private void ShowError(Exception error)
    {
        MessageText.Text = Ui.T(error.Message);
        WriteLog("Error: " + error.GetType().Name);
        if (!IsVisible) tray.ShowBalloonTip(5000, "MegaProxy", Ui.T("Operation failed. Open the application window."), Forms.ToolTipIcon.Error);
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    private Icon DisconnectedIcon()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1 ? lightTrayIcon : trayIcon;
    }
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!quitting) tray.Icon = engine?.Running == true ? connectedIcon : DisconnectedIcon();
    });
    private void WindowClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); } }
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private async void QuitClick(object sender, RoutedEventArgs e) => await QuitAsync();

    private async Task QuitAsync()
    {
        if (busy || quitting) return;
        quitting = true; timer.Stop(); SetBusy(true);
        diagnosticCancellation?.Cancel();
        try
        {
            while (testing) await Task.Delay(25);
            if (engine is not null) await engine.StopAsync();
            if (diagnosticEngine is not null) await diagnosticEngine.StopAsync();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, Ui.T("Disconnecting")); }
        finally
        {
            log.Changed -= LogChanged;
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            engine?.Dispose(); trayMenu.IsOpen = false; tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); lightTrayIcon.Dispose(); connectedIcon.Dispose();
            Application.Current.Shutdown();
        }
    }
}
