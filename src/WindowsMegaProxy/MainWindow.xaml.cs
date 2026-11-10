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
    public static readonly DependencyProperty RulesEditableProperty = DependencyProperty.Register(nameof(RulesEditable), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool RulesEditable { get => (bool)GetValue(RulesEditableProperty); private set => SetValue(RulesEditableProperty, value); }
    private readonly ServiceClient? service;
    private bool canEdit = true, refreshingService;
    private bool serviceAvailable, serviceTesting;
    private string? serviceInstance;
    private string? serviceAccessDiagnostic;
    private readonly Func<Task<Settings>> loadLegacy;
    private readonly Task<Settings>? initialSettings;
    private readonly Task<ServiceReply>? initialServiceReply;
    private readonly bool customAutostart;
    private readonly System.Diagnostics.Stopwatch startup = System.Diagnostics.Stopwatch.StartNew();
    private long serviceRevision;
    private readonly string? installScript = FindInstallScript();
    private Settings settings;
    private readonly ObservableCollection<RoutingRule> rules;
    private readonly Forms.NotifyIcon tray;
    private readonly Icon trayIcon;
    private readonly Icon lightTrayIcon;
    private readonly Icon connectedIcon;
    private readonly ContextMenu trayMenu;
    private readonly MenuItem trayOpen, trayConnect, trayExit;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ConnectionController connection;
    private IEngine? engine => connection.Engine;
    private bool busy => connection.IsBusy;
    private bool testing => connection.IsTesting;
    private readonly ObservableCollection<ConnectionRow> connectionRows = new();
    private readonly ListCollectionView connectionView;
    private readonly bool startInTray;
    private readonly SettingsWriter settingsWriter;
    private readonly BackgroundLog logWriter;
    private SettingsAnalysis? analysis, durable;
    private Task settingsUpdate = Task.CompletedTask, analysisTask = Task.CompletedTask;
    private long settingsRevision, durableRevision, analysisRevision, logVersion, displayedLogVersion;
    private bool settingsReady, settingsLoading, readingLog;
    private readonly object diagnosticGate = new();
    private readonly System.Collections.Generic.Queue<string> diagnosticEntries = new();
    private int diagnosticLength;
    private bool diagnosticWriting;
    private Task diagnosticTextUpdate = Task.CompletedTask;
    private readonly System.Collections.Generic.HashSet<Task> fileOperations = [];
    private readonly DiagnosticLog log;
    private readonly Func<bool, Task> setAutostart;
    private readonly Func<Task<bool>> getAutostart;
    private bool applyingAutostart;
    private bool speedRunning, diagnosticSucceeded;
    private readonly Func<Task> diagnosticsOnEnter;
    private Settings? checkedSettings;
    private DiagnosticResult? lastDiagnosticResult;
    private (SpeedResult Direct, SpeedResult Profile)? lastSpeedResults;
    private long previousUpload, previousDownload;
    private DateTimeOffset previousTrafficTime;
    private bool polling, quitting, updatingUi, restartRequested, activationRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(RestartMessage);
        ChangeWindowMessageFilterEx(handle, ChannelControl.ActivateMessage, 1, IntPtr.Zero);
    }

    private IntPtr RestartMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == ChannelControl.ActivateMessage)
        {
            handled = true;
            activationRequested = true;
            Dispatcher.BeginInvoke(ShowWindow);
            return new IntPtr(0x487);
        }
        if (message != 0x8487) return IntPtr.Zero;
        handled = true;
        Dispatcher.BeginInvoke(async () =>
        {
            if (restartRequested || quitting) return;
            restartRequested = true;
            await QuitAsync();
        });
        return new IntPtr(0x487);
    }

    public MainWindow(bool startInTray, Action<Settings>? persistSettings = null, DiagnosticLog? diagnosticLog = null,
        Func<bool, Task>? setAutostart = null, Func<Task<bool>>? getAutostart = null, Func<Task>? diagnosticsOnEnter = null, ConnectionController? connectionController = null,
        ServiceClient? serviceClient = null, Func<Task<Settings>>? loadLegacy = null, bool standalone = false, AppUpdateManager? appUpdates = null,
        Func<string, Task>? installAppUpdate = null, Func<string?>? chooseAppUpdate = null)
    {
        service = serviceClient ?? (persistSettings is null && !standalone ? new ServiceClient() : null);
        this.loadLegacy = loadLegacy ?? (() => Task.Run<Settings>(standalone ? StartupConfiguration.LoadLocal : SettingsStore.Load));
        automaticUpdateChecks = persistSettings is null && loadLegacy is null;
        if (persistSettings is null)
        {
            if (service is null) initialSettings = TrackFileAsync(this.loadLegacy());
            else initialServiceReply = TrackFileAsync(Task.Run(() => service.InitializeAsync(this.loadLegacy, updatesStopping.Token), updatesStopping.Token));
        }
        this.appUpdates = appUpdates ?? (automaticUpdateChecks ? new AppUpdateManager() : null);
        this.installAppUpdate = installAppUpdate ?? (action => PendingAppUpdate.RequestAsync(action));
        this.chooseAppUpdate = chooseAppUpdate ?? (() =>
        {
            var dialog = new UpdateInstallWindow(service is not null) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Action : null;
        });
        canEdit = service is null;
        connection = connectionController ?? new ConnectionController(CreateEngine);
        settingsWriter = new SettingsWriter(persistSettings ?? (standalone ? StartupConfiguration.SaveLocal : value =>
        {
            var reply = service!.CallAsync(new("save", Settings: value, Revision: Interlocked.Read(ref serviceRevision))).GetAwaiter().GetResult();
            Interlocked.Exchange(ref serviceRevision, reply.Revision);
        }));
        this.setAutostart = setAutostart ?? (_ => Task.CompletedTask);
        customAutostart = getAutostart is not null;
        this.getAutostart = getAutostart ?? (service is null ? () => Task.FromResult(StartupConfiguration.Mode == "login") : async () => (await service.CallAsync(new("status"))).AutoStart);
        this.diagnosticsOnEnter = diagnosticsOnEnter ?? (() => RunConnectionTestAsync());
        // Preserve existing log history until the saved limit has been loaded.
        log = diagnosticLog ?? new DiagnosticLog(Path.Combine(standalone ? StartupConfiguration.LocalDirectory : SettingsStore.DirectoryPath, "logs"), initialLimit: 100);
        logWriter = new BackgroundLog(log);
        logWriter.Failed += _ => Dispatcher.BeginInvoke(() => { if (!quitting) MessageText.Text = Ui.T("Could not write the log to disk."); });
        settings = new Settings();
        UiTheme.Apply(settings.Language, settings.Theme, this);
        InitializeComponent();
        Title = AppTitle.Text = AppIdentity.DisplayName;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri($"pack://application:,,,/{AppIdentity.Name};component/Assets/{AppIdentity.IconPrefix}app.ico"));
        BuildInformationText.Text = BuildInformation.Display;
        connectionView = new ListCollectionView(connectionRows) { IsLiveSorting = true, IsLiveGrouping = true };
        var grouping = new PropertyGroupDescription(nameof(ConnectionRow.RouteGroup));
        grouping.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        connectionView.GroupDescriptions.Add(grouping);
        ConnectionsGrid.ItemsSource = connectionView;
        this.startInTray = startInTray;
        log.Changed += LogChanged;
        WriteLog("Application started.");
        rules = new ObservableCollection<RoutingRule>();
        settingsLoading = true;
        ApplySettingsView(settings);
        RulesList.ItemsSource = rules;
        trayIcon = LoadIcon("tray.ico");
        lightTrayIcon = LoadIcon("tray-light.ico");
        connectedIcon = LoadIcon("app.ico");
        trayOpen = new MenuItem { Header = BrandLabel("Open MegaProxy") };
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
        tray = new Forms.NotifyIcon { Icon = DisconnectedIcon(), Text = BrandLabel("MegaProxy — disconnected"), Visible = true };
        tray.MouseUp += (_, args) => { if (args.Button == Forms.MouseButtons.Right) trayMenu.IsOpen = true; };
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.BalloonTipClicked += UpdateNotificationClicked;
        if (this.appUpdates is not null)
        {
            this.appUpdates.Changed += () => Dispatcher.BeginInvoke(() => { if (!quitting) UpdateAppUpdatesView(); });
            this.appUpdates.Log += value => Dispatcher.BeginInvoke(() => { if (!quitting) WriteLog(value); });
        }
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        timer.Tick += async (_, _) =>
        {
            await RefreshServiceStateAsync();
            await RefreshServiceAsync();
            await RefreshConnectionsAsync();
            await RefreshLogAsync();
            await RefreshSubscriptionAsync();
            await RunAppUpdateCheckAsync(manual: false);
        };
        timer.Start();
        DefaultActionBox.SelectionChanged += SettingsChanged;
        BypassLanBox.Click += SettingsChanged;
        AutoConnectBox.Click += SettingsChanged;
        ResetExistingTcpBox.Click += SettingsChanged;
        AutostartBox.Click += AutostartChanged;
        OperatingModeBox.SelectedValue = StartupConfiguration.Mode;
        if (UserInstallation.IsUser) OperatingModeBox.Items.RemoveAt(2);
        OperatingModeBox.SelectionChanged += OperatingModeChanged;
        LogSizeBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => SaveLogSize());
        LanguageBox.SelectionChanged += PreferencesChanged;
        ThemeBox.SelectionChanged += PreferencesChanged;
        TlsFingerprintBox.SelectionChanged += PreferencesChanged;
        BootstrapDnsBox.SelectionChanged += PreferencesChanged;
        rules.CollectionChanged += SettingsChanged;
        Sections.SelectionChanged += SectionsChanged;
        connection.Changed += () =>
        {
            if (Dispatcher.CheckAccess()) SetBusy(busy);
            else Dispatcher.BeginInvoke(() => SetBusy(busy));
        };
        settingsLoading = true;
        Sections.IsEnabled = false;
        SetBusy(busy);
        connection.UnexpectedExit += () => Dispatcher.BeginInvoke(async () =>
        {
            if (quitting) return;
            SetConnected(false);
            MessageText.Text = Ui.T("The core exited unexpectedly. Connections are no longer protected by TUN.");
            updateNoticeVersion = null;
            tray.ShowBalloonTip(5000, "MegaProxy", Ui.T("The core exited. TUN is disconnected."), Forms.ToolTipIcon.Warning);
            if (service is not null) { connection.Cancel(); if (engine is not null) connection.Attach(engine); return; }
            try { await connection.DisconnectAsync(); }
            catch (Exception error) { ShowError(error); }
        });
        WriteLog($"Startup: window constructed in {startup.ElapsedMilliseconds} ms.");
        ContentRendered += FirstContentRendered;
    }

    private void FirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= FirstContentRendered;
        WriteLog($"Startup: first window render after {startup.ElapsedMilliseconds} ms.");
    }

    internal void RecordStartupPreparation(long milliseconds) => WriteLog($"Startup: pre-window checks took {milliseconds} ms.");

    private void OpenGitHub(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowError(error); }
        e.Handled = true;
    }

    private static string? FindInstallScript()
    {
        if (!AppIdentity.IsDev) return null;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && directory is not null; i++, directory = directory.Parent)
        {
            string script = Path.Combine(directory.FullName, "install.cmd");
            if (File.Exists(script)) return script;
        }
        return null;
    }

    private async void InstallServiceClick(object sender, RoutedEventArgs e)
    {
        if (installScript is null) return;
        InstallServiceButton.IsEnabled = false;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(installScript)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(installScript)!
            };
            start.Arguments = "--dev --mode service";
            using var installer = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start the service installer.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode != 0) throw new IOException(AppIdentity.IsDev
                ? "Service installation failed. Run install.cmd --dev to see the details."
                : "Service installation failed. Repair the installation using the installer.");
            await RefreshServiceAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { InstallServiceButton.IsEnabled = true; }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (startInTray && !activationRequested) Hide();
        bool autoConnect = false;
        try
        {
            Settings loaded;
            bool? initialAutostart = null;
            if (service is null) loaded = await (initialSettings ?? TrackFileAsync(this.loadLegacy()));
            else
            {
                var reply = await (initialServiceReply ?? service.InitializeAsync(this.loadLegacy));
                initialAutostart = reply.AutoStart;
                canEdit = reply.CanEdit;
                serviceAvailable = true; serviceTesting = reply.Testing; serviceInstance = reply.InstanceId;
                loaded = reply.Settings ?? new Settings();
                serviceRevision = reply.Revision;
                RecordServiceAccess();
                var remote = (RemoteEngine)CreateEngine();
                remote.Apply(reply);
                connection.Attach(remote);
            }
            if (quitting) return;
            WriteLog($"Startup: settings received after {startup.ElapsedMilliseconds} ms.");
            ApplySettings(loaded);
            await analysisTask;
            ObserveLogTask(logWriter.SetLimitAsync(settings.LogSizeMb));
            bool enabled = !customAutostart && initialAutostart is { } serviceAutostart ? serviceAutostart : await getAutostart();
            AutostartBox.IsChecked = enabled;
            autoConnect = canEdit && !serviceTesting && settings.AutoConnect;
            WriteLog($"Startup: interface ready after {startup.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException) when (quitting) { return; }
        catch (Exception error)
        {
            if (service is not null)
            {
                await RefreshInstallationStateAsync();
                ApplySettings(new Settings());
                LoseServiceConnection();
                MessageText.Text = ServiceConnectionMessage;
                RecordServiceFailure(error);
                InstallServiceButton.Visibility = installScript is not null && ClientAccess.Current().CanEdit ? Visibility.Visible : Visibility.Collapsed;
            }
            else ShowError(error);
        }
        finally { SetBusy(busy); }
        if (quitting) return;
        try
        {
            if (!await ResolveChannelAsync()) return;
            if (autoConnect && !busy && engine?.Running != true) await ToggleAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RefreshServiceAsync()
    {
        if (service is null || settingsLoading || refreshingService || busy || quitting) return;
        refreshingService = true;
        try
        {
            await RefreshInstallationStateAsync();
            var reply = await service.CallAsync(new("status"));
            if (quitting) return;
            bool firstBinding = serviceInstance is null;
            bool rebind = !serviceAvailable || reply.InstanceId != serviceInstance || engine is not RemoteEngine;
            bool reload = rebind || reply.Revision != Interlocked.Read(ref serviceRevision) || engine is RemoteEngine current && reply.Status?.Generation != current.Generation;
            if (rebind) LoseServiceConnection();
            if (reload)
            {
                if (!settingsUpdate.IsCompleted || !analysisTask.IsCompleted || testing)
                {
                    return;
                }
                canEdit = false; SetBusy(busy);
                reply = await service.InitializeAsync(loadLegacy);
                if (quitting) return;
                serviceRevision = reply.Revision;
                ApplySettings(reply.Settings ?? new Settings());
                await analysisTask;
                if (quitting) return;
                var attached = (RemoteEngine)CreateEngine();
                attached.Apply(reply);
                connection.Attach(attached);
            }
            serviceInstance = reply.InstanceId; serviceAvailable = true; serviceTesting = reply.Testing;
            canEdit = reply.CanEdit;
            RecordServiceAccess();
            UpdateServiceStateView();
            InstallServiceButton.Visibility = Visibility.Collapsed;
            if (rebind || MessageText.Text == ServiceUnavailableMessage || MessageText.Text == Ui.T("Update in progress") || MessageText.Text == Ui.T(ServiceProtocol.ConfigurationRecoveryMessage)) MessageText.Text = "";
            if (reply.Text is not null) MessageText.Text = Ui.T(reply.Text);
            if (engine is RemoteEngine remote)
            {
                remote.Apply(reply);
                if (!testing && (connection.State == ConnectionState.Connected) != remote.Running) connection.Attach(remote);
            }
            bool connected = engine?.Running == true;
            UpdateProfileSummary();
            string connectionLabel = Ui.T(engine is RemoteEngine { Recovering: true } ? "Recovering connection…" : connected ? "Connected" : "Disconnected");
            if (StatusText.Text != connectionLabel) { SetConnected(connected); StatusText.Text = connectionLabel; }
            SetBusy(busy);
            if (firstBinding && canEdit && !serviceTesting && settings.AutoConnect && engine?.Running != true && engine is not RemoteEngine { Recovering: true }) await ToggleAsync();
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        {
            RecordServiceFailure(error);
            LoseServiceConnection();
            MessageText.Text = ServiceConnectionMessage;
            SetBusy(busy);
        }
        finally { refreshingService = false; }
    }

    private void LoseServiceConnection()
    {
        serviceAvailable = false; serviceTesting = false; canEdit = false;
        UpdateServiceStateView();
        connection.Cancel();
        if (engine is RemoteEngine remote) { remote.Invalidate(); if (!busy) connection.Attach(remote); }
        connectionRows.Clear();
        previousTrafficTime = default;
        TrafficText.Text = "↑ —  ↓ —";
        ConnectionsHint.Text = Ui.T(ServiceUpdateInProgress ? "Update in progress" : "Service unavailable");
        StatusText.Text = Ui.T(ServiceUpdateInProgress ? "Update in progress" : "Connection unknown");
        UpdateConnectionDetails();
        tray.Icon = DisconnectedIcon();
        diagnosticSucceeded = false; checkedSettings = null; lastDiagnosticResult = null; lastSpeedResults = null;
        SpeedPanel.Visibility = Visibility.Collapsed; SpeedLockedText.Visibility = Visibility.Visible;
        DiagnosticDetailsPanel.Visibility = Visibility.Collapsed;
        TestStatusText.Text = Ui.T(ServiceUpdateInProgress ? "Update in progress" : "Service unavailable");
        SetBusy(busy);
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag) ?? throw new InvalidOperationException(Ui.T("Unknown settings value."));
    }

    private static Icon LoadIcon(string name)
    {
        using var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/{AppIdentity.Name};component/Assets/{AppIdentity.IconPrefix}{name}")).Stream;
        using var icon = new Icon(resource);
        return (Icon)icon.Clone();
    }

    private static string BrandLabel(string key) => Ui.T(key).Replace("MegaProxy", AppIdentity.DisplayName, StringComparison.Ordinal);
    private static string ServiceUnavailableMessage => Ui.T(AppIdentity.IsDev
        ? "The DEV host is unavailable. Restart it with run.cmd."
        : "Cannot communicate with the MegaProxy service. See the log for details. If the problem persists, repair the installation using the installer.");

    private void RecordServiceAccess()
    {
        string message = canEdit ? "Service IPC connected; configuration editing is allowed."
            : "Read-only mode: the service authenticated this client, but its Windows account or token is not allowed to edit configuration. An administrator account with an unrestricted token is required.";
        if (serviceAccessDiagnostic == message) return;
        serviceAccessDiagnostic = message;
        WriteLog(message);
    }

    private void RecordServiceFailure(Exception error)
    {
        string reason = error switch
        {
            UnauthorizedAccessException when error.Message == "The service pipe has an untrusted owner." => "The service pipe owner is untrusted; the client refused to communicate with it.",
            UnauthorizedAccessException => "Access denied while opening or authenticating the service pipe. Check its permissions and owner.",
            TimeoutException or OperationCanceledException => "The service pipe connection or request timed out.",
            System.Text.Json.JsonException => "The service returned an invalid or incompatible response.",
            _ => "The service pipe connection or request failed."
        };
        string message = $"Read-only mode: service IPC unavailable. Pipe={ServiceProtocol.PipeName}; {serviceState}; {error.GetType().Name}; HRESULT=0x{error.HResult:X8}. {reason}";
        if (serviceAccessDiagnostic == message) return;
        serviceAccessDiagnostic = message;
        WriteLog(message);
    }

    private Task SaveSettingsAsync() => CommitSettingsAsync(CaptureSettings());

    private async Task<T> TrackFileAsync<T>(Task<T> operation)
    {
        fileOperations.Add(operation);
        try { return await operation; }
        finally { fileOperations.Remove(operation); }
    }

    private async Task TrackFileAsync(Task operation)
    {
        fileOperations.Add(operation);
        try { await operation; }
        finally { fileOperations.Remove(operation); }
    }

    private Settings CaptureSettings()
    {
        var updated = PortableConfiguration.Copy(settings);
        updated.DefaultAction = (string)((ComboBoxItem)DefaultActionBox.SelectedItem).Tag;
        updated.BypassLan = BypassLanBox.IsChecked == true;
        updated.AutoConnect = AutoConnectBox.IsChecked == true;
        updated.ResetExistingTcpConnections = ResetExistingTcpBox.IsChecked == true;
        updated.ShowAllConnections = ShowAllConnectionsBox.IsChecked == true;
        updated.Language = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;
        updated.Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag;
        updated.TlsFingerprint = (string)((ComboBoxItem)TlsFingerprintBox.SelectedItem).Tag;
        updated.BootstrapDnsProvider = (string)((ComboBoxItem)BootstrapDnsBox.SelectedItem).Tag;
        if (!int.TryParse(MtuBox.Text, out int mtu) || mtu is < 1280 or > 65535)
            throw new ArgumentException(Ui.T("MTU must be between 1280 and 65535 bytes."));
        updated.TunMtu = mtu;
        updated.TrafficUnits = (string)((ComboBoxItem)TrafficUnitsBox.SelectedItem).Tag;
        updated.SharedOptions["ssh"] ??= new System.Text.Json.Nodes.JsonObject();
        updated.SharedOptions["ssh"]!["authMode"] = (string)((ComboBoxItem)SshAuthModeBox.SelectedItem).Tag;
        if (!applyingAutostart) updated.LaunchAtSignIn = AutostartBox.IsChecked == true;
        updated.Rules = rules.Select(rule => rule.Copy()).ToList();
        return updated;
    }

    private Task CommitSettingsAsync(Settings updated)
    {
        if (!canEdit) throw new UnauthorizedAccessException("Read-only mode");
        long revision = ++settingsRevision;
        ++analysisRevision;
        settingsReady = analysis is not null && SettingsAnalysis.SameNetwork(analysis.Settings, updated);
        ApplySettingsView(updated);
        return settingsUpdate = CommitCoreAsync(updated, revision);
    }

    private async Task CommitCoreAsync(Settings updated, long revision)
    {
        try
        {
            var result = await settingsWriter.SaveAsync(updated);
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision >= durableRevision) { durable = result; durableRevision = revision; }
                if (revision != settingsRevision) return;
                analysis = result; settingsReady = true;
                ApplySettingsView(result.Settings);
                ObserveLogTask(logWriter.SetLimitAsync(result.Settings.LogSizeMb));
                WriteLog("Settings saved.");
            });
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (revision != settingsRevision || durable is null) return;
                analysis = durable; settingsReady = true;
                ApplySettingsView(durable.Settings);
            });
            throw;
        }
    }

    private async Task FlushSettingsAsync()
    {
        bool reportedFailure = settingsUpdate.IsFaulted;
        try { await settingsUpdate; }
        catch when (reportedFailure) { }
        await analysisTask;
        await settingsWriter.FlushAsync();
    }

    private void ApplySettings(Settings updated)
    {
        ++settingsRevision;
        settingsLoading = false;
        settingsReady = false;
        ApplySettingsView(updated);
        analysisTask = AnalyzeViewAsync(updated, ++analysisRevision);
    }

    private async Task AnalyzeViewAsync(Settings updated, long revision)
    {
        var previous = analysis;
        var result = await Task.Run(() => SettingsAnalysis.Prepare(updated, previous));
        await Dispatcher.InvokeAsync(() =>
        {
            if (revision != analysisRevision || !ReferenceEquals(settings, updated)) return;
            analysis = durable = result; settingsReady = true;
            Sections.IsEnabled = true;
            ApplySettingsView(result.Settings);
        });
    }

    private void ApplySettingsView(Settings updated)
    {
        bool first = ProfilePicker.ItemsSource is null;
        bool languageChanged = first || settings.Language != updated.Language;
        bool appearanceChanged = languageChanged || settings.Theme != updated.Theme;
        var shownProfiles = ProfilePicker.ItemsSource as System.Collections.Generic.IReadOnlyList<ProxyProfile>;
        bool profilesChanged = shownProfiles is null || shownProfiles.Count != updated.Profiles.Count ||
            !shownProfiles.Zip(updated.Profiles).All(pair => System.Text.Json.Nodes.JsonNode.DeepEquals(pair.First.Data, pair.Second.Data));
        bool rulesChanged = first || rules.Count != updated.Rules.Count || !rules.Zip(updated.Rules).All(pair =>
            pair.First.Source == pair.Second.Source && pair.First.Path == pair.Second.Path && pair.First.Destination == pair.Second.Destination &&
            pair.First.Action == pair.Second.Action && pair.First.ProfileId == pair.Second.ProfileId && pair.First.Enabled == pair.Second.Enabled && pair.First.Name == pair.Second.Name && pair.First.PodkopThroughProxy == pair.Second.PodkopThroughProxy);
        string? selected = (ProfilesList.SelectedItem as ProxyProfile)?.Id;
        int selectedRule = RulesList.SelectedIndex;
        updatingUi = true;
        try
        {
            settings = updated;
            if (appearanceChanged) UiTheme.Apply(updated.Language, updated.Theme, this);
            SelectTag(LanguageBox, updated.Language);
            SelectTag(ThemeBox, updated.Theme);
            DefaultFingerprintItem.Content = Ui.F("Default ({0})", TlsFingerprintBox.Items.Cast<ComboBoxItem>()
                .First(item => (string)item.Tag == Configuration.DefaultTlsFingerprint).Content.ToString()!.Trim());
            SelectTag(TlsFingerprintBox, updated.TlsFingerprint);
            SelectTag(BootstrapDnsBox, updated.BootstrapDnsProvider);
            MtuBox.Text = updated.TunMtu.ToString();
            SelectTag(TrafficUnitsBox, updated.TrafficUnits);
            string authMode = SshConfiguration.AuthMode(updated);
            if (!SshAuthModeBox.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == authMode))
                SshAuthModeBox.Items.Add(new ComboBoxItem { Content = authMode, Tag = authMode });
            SelectTag(SshAuthModeBox, authMode);
            ShowAllConnectionsBox.IsChecked = updated.ShowAllConnections;
            if (profilesChanged)
            {
                ProfilePicker.ItemsSource = ProfilesList.ItemsSource = settings.Profiles;
                ProfilesList.SelectedItem = settings.Profiles.FirstOrDefault(profile => profile.Id == selected) ?? settings.ActiveProfile;
            }
            ProfilePicker.SelectedItem = ((System.Collections.Generic.IEnumerable<ProxyProfile>)ProfilePicker.ItemsSource!).FirstOrDefault(profile => profile.Id == settings.ActiveProfileId);
            SelectTag(DefaultActionBox, settings.DefaultAction);
            BypassLanBox.IsChecked = settings.BypassLan;
            AutoConnectBox.IsChecked = settings.AutoConnect;
            ResetExistingTcpBox.IsChecked = settings.ResetExistingTcpConnections;
            AutostartBox.IsChecked = settings.LaunchAtSignIn == true;
            LogSizeBox.Text = settings.LogSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (rulesChanged)
            {
                rules.Clear();
                foreach (var rule in settings.Rules) rules.Add(rule);
                RulesList.SelectedIndex = Math.Min(selectedRule, rules.Count - 1);
            }
        }
        finally { updatingUi = false; }
        UpdateProfileSummary();
        if (tray is not null)
        {
            bool connected = engine?.Running == true;
            bool unavailable = service is not null && !serviceAvailable;
            StatusText.Text = Ui.T(unavailable ? "Connection unknown" : connected ? "Connected" : "Disconnected");
            trayOpen.Header = BrandLabel("Open MegaProxy");
            trayExit.Header = Ui.T("Exit");
            SetBusy(busy);
        }
        if (languageChanged)
        {
            foreach (var profile in (System.Collections.Generic.IEnumerable<ProxyProfile>)ProfilePicker.ItemsSource!) profile.RefreshLanguage();
            foreach (var rule in rules) rule.RefreshLanguage();
            SpeedSizeChanged(this, null!);
            if (diagnosticSucceeded && lastDiagnosticResult is not null && checkedSettings is not null) CompleteDiagnostics(lastDiagnosticResult, checkedSettings);
            if (lastSpeedResults is { } results) UpdateSpeedResults(results.Direct, results.Profile);
        }
    }

    private void UpdateProfileSummary()
    {
        bool connected = engine?.Running == true;
        var profile = connected ? engine!.AppliedSettings?.ActiveProfile : settings.ActiveProfile;
        ProfileSummary.Text = profile is null ? Ui.T("Add or import a profile on the Profiles tab")
            : profile.Supported ? profile.Name + " · " + profile.Description : profile.Availability;
        if (connected && engine!.UsedHttp3Fallback)
            ProfileSummary.Text += " · " + Ui.T("HTTP/3 unavailable; using HTTPS/TCP");
        string? error = ConnectionError();
        ConnectButton.IsEnabled = canEdit && (connected || error is null);
        ConnectButton.ToolTip = connected ? null : error;
        if (!connected && profile is not null && error is not null) ProfileSummary.Text += " · " + error;
        UpdateConnectionDetails();
    }

    private bool CanConnect() => canEdit && !serviceTesting && !settingsLoading && settingsReady && ConnectionError() is null;

    private IEngine CreateEngine()
    {
        if (service is not null)
            return new RemoteEngine(service, async update => await Dispatcher.InvokeAsync(() => new SshHostKeyWindow(update) { Owner = this }.ShowDialog() == true));
        var result = new Engine(AppIdentity.IsDev && !UserInstallation.IsUser ? null : Path.Combine(StartupConfiguration.LocalDirectory, "runtime"), allowEnvironmentOverrides: AppIdentity.IsDev && !UserInstallation.IsUser)
        {
            ConfirmSshHostKeyChange = async update => await Dispatcher.InvokeAsync(() => new SshHostKeyWindow(update) { Owner = this }.ShowDialog() == true),
            SaveSshHostKey = SaveSshHostKey
        };
        result.Log += WriteLog;
        result.RuntimeChanged += () => Dispatcher.BeginInvoke(() => { if (!quitting) { UpdateProfileSummary(); SetBusy(busy); } });
        return result;
    }

    private async Task SaveSshHostKey(SshHostKeyUpdate update)
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(() => SaveSshHostKey(update)).Task.Unwrap(); return; }
        var updated = PortableConfiguration.Copy(settings);
        var profile = updated.Profiles.FirstOrDefault(item => item.Id == update.ProfileId);
        if (!SshConfiguration.ApplyKey(profile, update))
            throw new InvalidOperationException(Ui.T("The SSH profile changed during key verification. Connect again."));
        await CommitSettingsAsync(updated);
        var persisted = durable?.Settings.Profiles.FirstOrDefault(item => item.Id == update.ProfileId);
        var endpoint = update.Hop == "jump" ? persisted?.Proxy["jump"] : persisted?.Proxy;
        if (endpoint?["trustedHostKey"]?.ToString() != update.Fingerprint)
            throw new InvalidOperationException(Ui.T("The SSH profile changed during key verification. Connect again."));
        WriteLog("Saved the verified SSH server key.");
    }

    private string? ConnectionError() => canEdit && settingsReady ? analysis?.Error : null;

    private async void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!canEdit || updatingUi || quitting || ProfilePicker.SelectedItem is not ProxyProfile profile) return;
        try
        {
            var updated = CaptureSettings();
            updated.ActiveProfileId = profile.Id;
            await CommitSettingsAsync(updated);
        }
        catch (Exception error) { ShowError(error); }
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
    private async void EditProfile(ProxyProfile profile, bool isNew)
    {
        if (!canEdit) return;
        long expected = settingsRevision;
        var dialog = new ProfileWindow(profile, settings.Profiles) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        try
        {
            if (!isNew && expected != settingsRevision) throw new IOException(Ui.T("Settings changed while editing. Open the profile again."));
            var updated = CaptureSettings();
            if (isNew) updated.Profiles.Add(dialog.Profile);
            else updated.Profiles[updated.Profiles.FindIndex(item => item.Id == profile.Id)] = dialog.Profile;
            if (updated.ActiveProfile is null) updated.ActiveProfileId = dialog.Profile.Id;
            await CommitSettingsAsync(updated);
            ProfilesList.SelectedItem = settings.Profiles.First(item => item.Id == dialog.Profile.Id);
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit) return;
        if (ProfilesList.SelectedItem is not ProxyProfile profile) return;
        try
        {
            if (rules.Any(rule => rule.ProfileId == profile.Id)) throw new InvalidOperationException(Ui.T("The profile is used by rules. Change those rules first."));
            if (ContainsProfileReference(settings.SharedOptions, profile.Id)) throw new InvalidOperationException(Ui.T("The profile is used by another client's settings. Remove those assignments in that client first."));
            if (MessageBox.Show(this, Ui.F("Delete profile “{0}”?", profile.Name), Ui.T("Profiles"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var updated = CaptureSettings();
            updated.Profiles.RemoveAll(item => item.Id == profile.Id);
            if (updated.ActiveProfile is null) updated.ActiveProfileId = updated.Profiles.FirstOrDefault()?.Id;
            await CommitSettingsAsync(updated);
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

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit) return;
        if (busy || quitting) return;
        var dialog = new OpenFileDialog { Filter = "Supported configurations|*.json;*.txt|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string text = await TrackFileAsync(Task.Run(() => PortableConfiguration.ReadFile(dialog.FileName)));
            if (quitting) return;
            long revision = settingsRevision;
            var current = CaptureSettings();
            var review = await TrackFileAsync(Task.Run(() => new ImportReview(ProfileTransfer.Parse(text), current)));
            if (quitting) return;
            var options = new ImportWindow(review) { Owner = this };
            if (options.ShowDialog() != true) return;
            var selected = options.Selected;
            var remove = options.RemoveIds;
            bool applyGlobals = options.ApplyGlobals;
            var updated = await TrackFileAsync(Task.Run(() => review.Apply(selected, applyGlobals, remove)));
            if (quitting) return;
            if (revision != settingsRevision) throw new IOException(Ui.T("Settings changed during import. Review the file again."));
            await CommitSettingsAsync(updated);
            int unsupported = settings.Profiles.Count(profile => !profile.Supported);
            MessageText.Text = Ui.F("Imported {0} profiles; unsupported connection types: {1}. Windows startup was not changed.", settings.Profiles.Count, unsupported);
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit) return;
        if (quitting) return;
        try
        {
            var options = new ExportWindow { Owner = this };
            if (options.ShowDialog() != true) return;
            var snapshot = CaptureSettings();
            bool proxyList = options.ProxyList;
            bool passwords = options.IncludePasswords, privateKeys = options.IncludePrivateKeys;
            string document = await TrackFileAsync(Task.Run(() => proxyList ? ProfileTransfer.ExportProxyList(snapshot, passwords)
                : PortableConfiguration.Export(snapshot, passwords, privateKeys).ToJsonString(Configuration.JsonOptions)));
            if (quitting) return;
            var dialog = new SaveFileDialog { FileName = proxyList ? "ProxyList.txt" : "MegaProxy.json", DefaultExt = proxyList ? ".txt" : ".json", Filter = proxyList ? "ProxyList|*.txt" : "MegaProxy JSON|*.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            await TrackFileAsync(Task.Run(() => SettingsStore.WriteAtomic(dialog.FileName, document)));
            MessageText.Text = Ui.T("Configuration exported.");
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void ConnectClick(object sender, RoutedEventArgs e) => await ToggleAsync();

    private async Task ToggleAsync()
    {
        if (!canEdit || serviceTesting) return;
        if (connection.State == ConnectionState.Connecting) { connection.Cancel(); return; }
        if (busy || quitting || (testing && engine?.Running != true)) return;

        try
        {
            if (engine?.Running == true || engine is RemoteEngine { Recovering: true })
            {
                StatusText.Text = Ui.T("Disconnecting…");
                await connection.DisconnectAsync();
                SetConnected(false);
            }
            else
            {
                if (!await ResolveChannelAsync()) return;
                if (settings.ActiveProfile is { Type: "HTTPS" or "HTTPS_JUMP" or "MASQUE" } selected &&
                    (selected.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() == true || selected.Type == "HTTPS_JUMP" && selected.Proxy["jump"]?["allowInvalidProxyCertificate"]?.GetValue<bool>() == true) &&
                    MessageBox.Show(this, Ui.T("Proxy certificate verification is disabled in this profile. Connect with this exception?"), Ui.T("Proxy certificate"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await SaveSettingsAsync();
                if (settings.ActiveProfile is { Type: "SSH" } ssh && ssh.Proxy["acceptAnyHostKey"]?.GetValue<bool>() == true &&
                    MessageBox.Show(this, Ui.T("SSH server identity verification is disabled in this profile. Connect with this exception?"), "SSH", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                StatusText.Text = Ui.T("Connecting…");
                await connection.ConnectAsync(settings);
                SetConnected(true); timer.Start();
                previousTrafficTime = default;
                MessageText.Text = "";
                await RefreshConnectionsAsync();
            }
        }
        catch (OperationCanceledException) { SetConnected(false); }
        catch (Exception error) { SetConnected(false); ShowError(error); }
        finally { SetBusy(busy); }
    }

    private void SetBusy(bool value)
    {
        UpdateServiceStateView();
        bool connecting = connection.State == ConnectionState.Connecting;
        bool recovering = engine is RemoteEngine { Recovering: true };
        ConnectButton.IsEnabled = canEdit && !quitting && (connecting || !value && (recovering || engine?.Running == true || (!testing && CanConnect())));
        ConnectButton.Content = trayConnect.Header = Ui.T(connecting || recovering ? "Cancel connection" : engine?.Running == true ? "Disconnect" : "Connect");
        trayConnect.IsEnabled = ConnectButton.IsEnabled;
        bool editable = canEdit && !quitting && !settingsLoading && !installingAppUpdate;
        RulesEditable = editable && !changingRules;
        foreach (var panel in new Panel[] { ProfilePanel, RulesPanel })
            foreach (var button in Descendants<Button>(panel)) button.IsEnabled = editable;
        foreach (var control in new Control[] { DefaultActionBox, BypassLanBox, LanguageBox, ThemeBox, TlsFingerprintBox, BootstrapDnsBox, MtuBox, TrafficUnitsBox, SshAuthModeBox, AutoConnectBox, ResetExistingTcpBox, LogSizeBox, ShowAllConnectionsBox }) control.IsEnabled = editable;
        AutostartBox.IsEnabled = editable && !applyingAutostart && !AppIdentity.IsDev;
        OperatingModeBox.IsEnabled = editable && !busy && scheduledAppUpdate?.FilesQueued != true && (File.Exists(Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable)) || AppIdentity.IsDev && installScript is not null);
        OperatingModeBox.ToolTip = scheduledAppUpdate?.FilesQueued == true ? Ui.T("Restart Windows to finish the update. File replacement has already been scheduled.") : null;
        if (AppIdentity.IsDev) AutostartBox.ToolTip = Ui.T("Development builds are started with run.cmd.");
        ReadOnlyModeText.Text = canEdit ? "" : " · Read-only mode";
        ExportLogButton.IsEnabled = !quitting;
        SettingsDetailHost.IsEnabled = editable && !busy && !settingsNavigationBusy;
        UpdateAppUpdatesView();
        ProfilePicker.IsEnabled = editable;
        TestConnectionButton.IsEnabled = canEdit && !value && !testing && (engine?.Running == true || CanConnect());
        SpeedOptionsPanel.IsEnabled = !testing;
        SpeedTestButton.IsEnabled = canEdit && !value && diagnosticSucceeded && (!testing || speedRunning);
        ReconnectButton.IsEnabled = canEdit && !value && !testing && engine?.Running == true && CanConnect();
        bool changed = engine?.Running == true && (!settingsReady || engine.AppliedSignature != analysis?.Signature);
        ReconnectButton.Content = changed ? Ui.T("Reconnect •") : Ui.T("Reconnect");
        System.Windows.Automation.AutomationProperties.SetName(ReconnectButton,
            changed ? Ui.T("Reconnect and apply changes") : Ui.T("Reconnect"));
        ReconnectButton.ToolTip = changed ? Ui.T("Apply profile and rule changes") : Ui.T("Reconnect using current settings");
        UpdateListCommands();
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private void SetConnected(bool value)
    {
        StatusText.Text = value ? Ui.T("Connected") : Ui.T("Disconnected");
        ConnectButton.Content = trayConnect.Header = value ? Ui.T("Disconnect") : Ui.T("Connect");
        tray.Icon = value ? connectedIcon : DisconnectedIcon();
        UpdateProfileSummary();
        WriteLog(value ? "TUN connected." : "TUN disconnected.");
        if (!value)
        {
            connectionRows.Clear(); ConnectionsHint.Text = Ui.T("TUN disconnected.");
            TrafficText.Text = "↑ —  ↓ —";
        }
    }

    private bool CanPollConnections() => IsVisible && WindowState != WindowState.Minimized &&
        ((TabItem)Sections.Items[0]).IsSelected && connection.State == ConnectionState.Connected && engine?.Running == true;

    private async Task RefreshConnectionsAsync()
    {
        if (!canEdit || !CanPollConnections()) { previousTrafficTime = default; return; }
        if (polling) return;
        polling = true;
        var current = engine!;
        bool showAll = ShowAllConnectionsBox.IsChecked == true;
        string language = Ui.Language;
        try
        {
            var snapshot = await Task.Run(() => current.GetConnectionsAsync(showAll));
            if (current != engine || !CanPollConnections() || language != Ui.Language || showAll != (ShowAllConnectionsBox.IsChecked == true)) return;
            UpdateConnections(snapshot.Rows.Select(row => new ConnectionRow(row.Process, row.Destination, row.Network, row.Route,
                ConnectionSnapshot.Bytes(row.UploadedBytes, settings.TrafficUnits), ConnectionSnapshot.Bytes(row.DownloadedBytes, settings.TrafficUnits),
                row.RouteGroup, row.UploadedBytes, row.DownloadedBytes, row.Id)).ToArray());
            ConnectionsHint.Text = Ui.F("Shown: {0} of {1}.", snapshot.Rows.Count, snapshot.Total);
            var now = DateTimeOffset.UtcNow;
            if (previousTrafficTime != default)
            {
                double seconds = (now - previousTrafficTime).TotalSeconds;
                TrafficText.Text = $"↑ {ConnectionSnapshot.Bytes(Math.Max(0, snapshot.Upload - previousUpload) / seconds, settings.TrafficUnits)}/s   ↓ {ConnectionSnapshot.Bytes(Math.Max(0, snapshot.Download - previousDownload) / seconds, settings.TrafficUnits)}/s";
            }
            previousTrafficTime = now; previousUpload = snapshot.Upload; previousDownload = snapshot.Download;
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or ObjectDisposedException or System.Text.Json.JsonException or IOException or InvalidOperationException or FormatException)
        { ConnectionsHint.Text = Ui.T("Statistics are temporarily unavailable."); }
        finally { polling = false; }
    }

    private void UpdateConnections(System.Collections.Generic.IReadOnlyList<ConnectionRow> rows)
    {
        if (!ReferenceEquals(ConnectionsGrid.ItemsSource, connectionView)) ConnectionsGrid.ItemsSource = connectionView;
        var incoming = rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var existing = connectionRows.ToDictionary(row => row.Id, StringComparer.Ordinal);
        foreach (var row in connectionRows.ToArray())
        {
            bool copying = Keyboard.FocusedElement is TextBox { SelectionLength: > 0 } box && ReferenceEquals(box.DataContext, row);
            if (!incoming.ContainsKey(row.Id) && !copying) connectionRows.Remove(row);
        }
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Id, out var current)) current.Update(row);
            else connectionRows.Add(row);
        }
    }

    private async void SettingsChanged(object? sender, EventArgs e)
    {
        if (!canEdit || updatingUi || quitting) return;
        try
        {
            var updated = CaptureSettings();
            await CommitSettingsAsync(updated);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private async void AutostartChanged(object sender, RoutedEventArgs e)
    {
        if (AppIdentity.IsDev) return;
        if (!canEdit || updatingUi || applyingAutostart) return;
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
                await CommitSettingsAsync(updated);
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
        finally { applyingAutostart = false; AutostartBox.IsEnabled = canEdit; }
    }

    private void AddRuleClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit) return;
        var dialog = new RuleWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Rule is not null) rules.Add(dialog.Rule);
    }

    private void EditRuleClick(object sender, RoutedEventArgs e) => EditSelectedRule();

    private void EditSelectedRule()
    {
        if (!canEdit) return;
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
            if (node is TextBox or CheckBox) return;
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

    private async void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!canEdit || updatingUi || quitting) return;
        try { await CommitSettingsAsync(CaptureSettings()); }
        catch (Exception error) { ShowError(error); }
    }

    private async void ReconnectClick(object sender, RoutedEventArgs e)
    {
        if (busy || engine?.Running != true || testing) return;
        await ToggleAsync();
        if (engine?.Running != true && !quitting) await ToggleAsync();
    }

    private async void TestConnectionClick(object sender, RoutedEventArgs e) => await RunConnectionTestAsync();

    private async Task RunConnectionTestAsync()
    {
        if (!canEdit || serviceTesting) return;
        if (busy || testing || quitting) return;
        SetBusy(busy);
        diagnosticSucceeded = false;
        lastDiagnosticResult = null;
        lastSpeedResults = null;
        checkedSettings = null;
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
        CancellationToken testToken = default;
        try
        {
            var snapshot = PortableConfiguration.Copy(settings);
            if (engine?.Running != true)
            {
                if (snapshot.ActiveProfile is { Supported: false } unsupported) throw new InvalidOperationException(unsupported.Availability);
                if (snapshot.ActiveProfile?.Supported == true) snapshot.DefaultAction = "proxy";
            }
            await connection.RunDiagnosticsAsync(snapshot, async (target, token) =>
            {
                testToken = token;
                target.Log += AppendDiagnosticEntry;
                try
                {
                    if (target.HasProbeProfile && !target.Http3Selected)
                        await ProxyAuthenticationProbe.CheckAsync(target.AppliedSettings!.ActiveProfile,
                            message => { AppendDiagnosticEntry(message); WriteLog(message); }, token);
                    using var probe = target.CreateProbeClient(target.HasProbeProfile);
                    var details = await NetworkDiagnostics.CheckAsync(probe, target.HasProbeProfile,
                        message => { AppendDiagnosticEntry(message); WriteLog(message); }, token);
                    if (target.HasProbeProfile)
                    {
                        string http3 = await target.CheckHttp3Async(token);
                        details = details with { Http3 = http3 };
                        if (http3.Length > 0) { AppendDiagnosticEntry(http3); WriteLog(http3); }
                    }
                    token.ThrowIfCancellationRequested();
                    if (!quitting)
                    {
                        CompleteDiagnostics(details, target.AppliedSettings!);
                        TestStatusText.Text += " · " + ConnectionSnapshot.RouteName(target.SelectedOutbound, target.AppliedSettings!);
                        if (target.Http3Selected) TestStatusText.Text += " · HTTP/3";
                        else if (target.UsedHttp3Fallback) TestStatusText.Text += " · " + Ui.T("HTTP/3 unavailable; using HTTPS/TCP");
                    }
                }
                finally { target.Log -= AppendDiagnosticEntry; }
            });
        }
        catch (OperationCanceledException) when (quitting || testToken.IsCancellationRequested)
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
            if (!quitting)
            {
                TestProgress.Visibility = Visibility.Collapsed;
                SetBusy(busy);
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
        DiagnosticHttp3Label.Visibility = DiagnosticHttp3Text.Visibility;
        DiagnosticCountryPanel.Visibility = Visibility.Visible;
        DiagnosticCountryPanel.DataContext = ProfileAppearance.Country(details.Country ?? "");
        SpeedPanel.Visibility = Visibility.Visible;
        SpeedLockedText.Visibility = Visibility.Collapsed;
    }

    private async void SectionsChanged(object sender, SelectionChangedEventArgs e)
    {
        await RefreshLogAsync();
        if (e.Source != Sections || !e.AddedItems.Contains(DiagnosticsTab) || !DiagnosticsTab.IsSelected || !IsLoaded || updatingUi || quitting) return;
        if (busy || testing) return;
        await diagnosticsOnEnter();
    }

    private void SpeedSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedSizeBox?.SelectedItem is ComboBoxItem { Tag: string size } && SpeedTrafficText is not null)
            SpeedTrafficText.Text = Ui.F("Cloudflare: {0} MB download and upload per route, up to {1} MB of traffic.", size, int.Parse(size) * 4);
    }

    private async void SpeedTestClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit) return;
        if (speedRunning) { connection.Cancel(); return; }
        if (busy || testing || !diagnosticSucceeded || checkedSettings is null) return;
        speedRunning = true;
        SpeedTestButton.Content = Ui.T("Stop speed test");
        SetBusy(busy);
        SpeedResult direct = new("Without profile"), profile = new("Through profile");
        void Update(bool throughProfile, SpeedResult result)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Update(throughProfile, result)); return; }
            if (throughProfile) profile = result; else direct = result;
            UpdateSpeedResults(direct, profile);
        }
        try
        {
            if (engine?.Running == true && !ReferenceEquals(checkedSettings, engine.AppliedSettings) && engine.AppliedSignature != (await Task.Run(() => Configuration.Signature(checkedSettings))))
                throw new InvalidOperationException(Ui.T("The connection changed. Run diagnostics again before testing speed."));
            await connection.RunDiagnosticsAsync(checkedSettings, async (target, cancellation) =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var token = deadline.Token;
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
                        void LogSpeed(string message) { AppendDiagnosticEntry(message); WriteLog(message); }
                        if (protocol == "h3" && await target.MeasureHttp3Async(throughProfile, size, result => Update(throughProfile, result), LogSpeed, token) is { } h3)
                        {
                            Update(throughProfile, h3 with { Reputation = await IpReputation.CheckAsync(probe, h3.Ip, LogSpeed, token) });
                            continue;
                        }
                        var measured = await Task.Run(() => NetworkDiagnostics.MeasureAsync(probe, name, size, protocol == "h3" ? "h2" : protocol, throughProfile,
                            result => Update(throughProfile, result), message => { AppendDiagnosticEntry(message); WriteLog(message); }, token), token);
                        Update(throughProfile, measured with { Reputation = await IpReputation.CheckAsync(probe, measured.Ip, LogSpeed, token) });
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        Update(throughProfile, (throughProfile ? profile : direct) with { Status = "Error" });
                        AppendDiagnosticEntry(name + ": " + error.GetType().Name);
                    }
                }
            });
        }
        catch (OperationCanceledException) { AppendDiagnosticEntry("Speed test stopped."); Update(false, direct with { Status = "Stopped" }); Update(true, profile with { Status = "Stopped" }); }
        catch (Exception error) { AppendDiagnosticEntry("Diagnostic failed: " + error.GetType().Name); ShowError(error); }
        finally
        {
            speedRunning = false;
            if (!quitting) { SpeedTestButton.Content = Ui.T("Test speed"); SetBusy(busy); }
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
            new SpeedMetricRow(Ui.T("IP reputation (ProxyCheck)"), Ui.T(direct.Reputation), Ui.T(profile.Reputation)),
            new SpeedMetricRow(Ui.T("Status"), Ui.T(direct.Status), Ui.T(profile.Status))
        };
    }

    private void AppendDiagnosticEntry(string message)
    {
        if (quitting) return;
        string entry = $"{DateTime.Now:HH:mm:ss} {(message.Length > 8192 ? message[..8192] : message)}{Environment.NewLine}";
        lock (diagnosticGate)
        {
            diagnosticEntries.Enqueue(entry); diagnosticLength += entry.Length;
            while (diagnosticLength > 64000) diagnosticLength -= diagnosticEntries.Dequeue().Length;
            if (!diagnosticWriting) { diagnosticWriting = true; diagnosticTextUpdate = Task.Run(DrainDiagnosticTextAsync); }
        }
    }

    private async Task DrainDiagnosticTextAsync()
    {
        while (true)
        {
            string[] batch;
            lock (diagnosticGate)
            {
                if (diagnosticEntries.Count == 0) { diagnosticWriting = false; return; }
                batch = diagnosticEntries.ToArray(); diagnosticEntries.Clear(); diagnosticLength = 0;
            }
            string text = string.Concat(batch.Select(entry => DiagnosticLog.Redact(entry).TrimEnd('\r', '\n') + Environment.NewLine));
            await Dispatcher.InvokeAsync(() => { if (!quitting) LogAutoScroll.Append(TestLogText, text); });
        }
    }

    private void LogChanged() => Interlocked.Increment(ref logVersion);

    private void WriteLog(string message) => logWriter.Write(message);

    private async void ObserveLogTask(Task task)
    {
        try { await task; }
        catch (Exception) { if (!quitting) await Dispatcher.InvokeAsync(() => MessageText.Text = Ui.T("Could not write the log to disk.")); }
    }

    private async Task RefreshLogAsync()
    {
        if (quitting) return;
        long version = Interlocked.Read(ref logVersion);
        if (readingLog || !IsVisible || (service is null && version == displayedLogVersion) || !((TabItem)Sections.Items[^1]).IsSelected || LogText.SelectionLength > 0) return;
        readingLog = true;
        try
        {
            string text = await logWriter.ReadAsync(tailOnly: true);
            if (service is not null && serviceAvailable && canEdit)
            {
                try { text = "[Client]\n" + text + "\n[Service]\n" + ((await service.CallAsync(new("log"))).Text ?? ""); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException or Win32Exception)
                { RecordServiceFailure(error); text = await logWriter.ReadAsync(tailOnly: true); }
            }
            if (!quitting && IsVisible && ((TabItem)Sections.Items[^1]).IsSelected && LogText.SelectionLength == 0)
            {
                LogAutoScroll.Replace(LogText, text);
                displayedLogVersion = version;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageText.Text = Ui.T("Could not read the log."); }
        finally { readingLog = false; }
    }

    private async void ExportLogClick(object sender, RoutedEventArgs e)
    {
        if (quitting) return;
        var dialog = new SaveFileDialog { FileName = "MegaProxy.log", Filter = Ui.T("Text log|*.log"), AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (service is null || !serviceAvailable || !canEdit) await TrackFileAsync(logWriter.ExportAsync(dialog.FileName));
            else await TrackFileAsync(service.ExportLogAsync(dialog.FileName));
        }
        catch (Exception error) { ShowError(error); }
    }

    private void LogSizeChanged(object sender, KeyboardFocusChangedEventArgs e) => SaveLogSize();
    private void LogSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveLogSize(); e.Handled = true; }
    }

    private async void SaveLogSize()
    {
        if (!canEdit || updatingUi || quitting || !IsLoaded) return;
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
            await CommitSettingsAsync(updated);
            WriteLog("Log size changed.");
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void RuleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not RoutingRule rule) return;
        if (!RulesEditable || updatingUi || quitting) { box.IsChecked = rule.Enabled; return; }
        int index = rules.IndexOf(rule);
        if (index < 0) return;
        try
        {
            var updated = CaptureSettings();
            updated.Rules[index].Enabled = box.IsChecked == true;
            await CommitSettingsAsync(updated);
        }
        catch (Exception error) { box.IsChecked = rule.Enabled; ShowError(error); }
    }
    private void MoveRuleUpClick(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDownClick(object sender, RoutedEventArgs e) => MoveRule(1);
    private void MoveRule(int offset)
    {
        if (!canEdit) return;
        int index = RulesList.SelectedIndex;
        if (index < 0 || index + offset < 0 || index + offset >= rules.Count) return;
        rules.Move(index, index + offset); RulesList.SelectedIndex = index + offset;
    }
    private void ShowError(Exception error)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowError(error)); return; }
        MessageText.Text = Ui.T(error.Message);
        WriteLog("Error: " + error.GetType().Name);
        if (!IsVisible) { updateNoticeVersion = null; tray.ShowBalloonTip(5000, "MegaProxy", Ui.T("Operation failed. Open the application window."), Forms.ToolTipIcon.Error); }
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); _ = OpenPendingChannelConflictAsync(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, int message, uint action, IntPtr filter);
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
        if (quitting) return;
        quitting = true; timer.Stop(); SetBusy(true);
        subscriptionStopping.Cancel();
        updatesStopping.Cancel();
        connection.Cancel();
        try
        {
            await FlushSettingsFormAsync();
            await Task.WhenAll(connection.ShutdownAsync(), FlushSettingsAsync(), diagnosticTextUpdate, updateOperation,
                Task.WhenAll(fileOperations.ToArray()));
        }
        catch (OperationCanceledException) when (updatesStopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (installingAppUpdate) WriteLog("Application update handoff continues after shutdown failure: " + error.Message);
            else MessageBox.Show(this, error.Message, Ui.T("Disconnecting"));
        }
        finally
        {
            await logWriter.DisposeAsync();
            appUpdates?.Dispose(); updatesStopping.Dispose();
            log.Changed -= LogChanged;
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            trayMenu.IsOpen = false; tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); lightTrayIcon.Dispose(); connectedIcon.Dispose();
            Application.Current.Shutdown();
        }
    }
}
