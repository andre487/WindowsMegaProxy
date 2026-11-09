using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private readonly AppUpdateManager? appUpdates;
    private readonly bool automaticUpdateChecks;
    private readonly Func<string, Task> installAppUpdate;
    private readonly Func<string?> chooseAppUpdate;
    private PendingAppUpdate? scheduledAppUpdate;
    private readonly CancellationTokenSource updatesStopping = new();
    private Task updateOperation = Task.CompletedTask;
    private bool checkingAppUpdate, installingAppUpdate, changingUpdatePreference;
    private string? updateNoticeVersion;

    private void UpdateAppUpdatesView()
    {
        if (AutomaticUpdatesBox is null) return;
        changingUpdatePreference = true;
        AutomaticUpdatesBox.IsChecked = AppUpdatePolicy.Enabled(settings);
        UpdatesThroughProxyBox.IsChecked = AppUpdatePolicy.ThroughProxy(settings);
        changingUpdatePreference = false;
        AutomaticUpdatesBox.IsEnabled = canEdit && !quitting && !settingsLoading;
        UpdatesThroughProxyBox.IsEnabled = AutomaticUpdatesBox.IsEnabled && !checkingAppUpdate;
        InstalledVersionText.Text = Ui.F("Installed version: {0}", AppUpdateTrust.CurrentVersion.ToString(3));
        string status = installingAppUpdate ? "Preparing update…" : appUpdates?.StatusKey ?? "Update files are unavailable.";
        if (!installingAppUpdate && !checkingAppUpdate && appUpdates?.Ready != true && scheduledAppUpdate is null && AppUpdatePolicy.ThroughProxy(settings) && (engine?.Running != true || !engine.HasProbeProfile))
            status = "Connect MegaProxy to check or download updates through the proxy.";
        if (!installingAppUpdate && !checkingAppUpdate && scheduledAppUpdate is { } pending)
            status = pending.When == "unknown" ? "Installation status is unknown. You can retry through the elevated installer." : pending.When is "now" or "applying" ? "Installing update…" : pending.When == "failed" ? "Update installation was interrupted. Retry the installation." : pending.When == "launch" ? "Update scheduled for the next application start." : "Update scheduled for the next computer restart.";
        UpdateStatusText.Text = Ui.T(status);
        if (scheduledAppUpdate?.FilesQueued == true) UpdateStatusText.Text = Ui.T(ServiceUpdateInProgress ? "Update in progress" : "Restart Windows to finish the update. File replacement has already been scheduled.");
        UpdateErrorText.Text = appUpdates?.ErrorKey is { } error && error != status ? Ui.T(error) : "";
        UpdateErrorText.Visibility = UpdateErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool ready = appUpdates?.Ready == true || scheduledAppUpdate is not null;
        bool available = appUpdates?.Package is not null || scheduledAppUpdate is not null;
        bool newerDownload = appUpdates?.Package is { } offered && (scheduledAppUpdate is null || AppUpdateTrust.ParseVersion(offered.Version) > AppUpdateTrust.ParseVersion(scheduledAppUpdate.Version));
        string? offeredVersion = newerDownload ? appUpdates!.Package!.Version : scheduledAppUpdate?.Version ?? appUpdates?.Package?.Version;
        UpdateVersionText.Text = available ? Ui.F("Available version: {0}", offeredVersion!) : "";
        UpdateVersionText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleaseLinkText.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleaseLink.NavigateUri = available ? new Uri($"https://github.com/{AppUpdateTrust.Repository}/releases/tag/v{offeredVersion}") : null;
        DownloadUpdateButton.Visibility = (newerDownload || (scheduledAppUpdate?.When == "failed" && appUpdates?.Package is not null)) && appUpdates?.Ready != true ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = scheduledAppUpdate?.FilesQueued != true && scheduledAppUpdate?.When is not ("now" or "applying") && !checkingAppUpdate && !installingAppUpdate && !quitting;
        InstallUpdateButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = Ui.T(scheduledAppUpdate is null || (newerDownload && appUpdates?.Ready == true) || scheduledAppUpdate.When is "failed" or "unknown" ? "Install update" : "Change update timing");
        InstallUpdateButton.IsEnabled = ready && scheduledAppUpdate?.FilesQueued != true && scheduledAppUpdate?.When is not ("now" or "applying") && !AppIdentity.IsDev && !installingAppUpdate && !checkingAppUpdate && !quitting;
        InstallUpdateButton.ToolTip = AppIdentity.IsDev ? Ui.T("Development builds are updated with run.cmd.") : null;
        CheckUpdatesButton.IsEnabled = scheduledAppUpdate?.When is not ("now" or "applying") && !checkingAppUpdate && !installingAppUpdate && !quitting && !settingsLoading;
        bool transferring = checkingAppUpdate && (appUpdates?.StatusKey is "Checking for updates…" or "Downloading update…");
        CheckUpdatesButton.Content = Ui.T(transferring ? "Checking for updates…" : "Check now");
        UpdateDownloadProgress.IsIndeterminate = transferring && appUpdates?.StatusKey != "Downloading update…";
        UpdateDownloadProgress.Visibility = transferring ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = appUpdates?.Progress ?? 0;
    }

    private async void AutomaticUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (changingUpdatePreference || !canEdit || quitting || settingsLoading) return;
        try
        {
            var updated = CaptureSettings();
            updated.WindowsOptions[AppUpdatePolicy.Option] = AutomaticUpdatesBox.IsChecked == true;
            updated.WindowsOptions[AppUpdatePolicy.ProxyOption] = UpdatesThroughProxyBox.IsChecked == true;
            await CommitSettingsAsync(updated);
        }
        catch (Exception error) { ShowError(error); UpdateAppUpdatesView(); }
    }

    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await RunAppUpdateCheckAsync(manual: true);
    private async void DownloadUpdateClick(object sender, RoutedEventArgs e) => await RunAppUpdateCheckAsync(manual: true, download: true);

    private async Task RunAppUpdateCheckAsync(bool manual, bool download = false)
    {
        if (appUpdates is null || checkingAppUpdate || installingAppUpdate || quitting || settingsLoading) return;
        if (!manual && !automaticUpdateChecks) return;
        checkingAppUpdate = true;
        if (manual) WriteLog("Manual application update check requested.");
        UpdateAppUpdatesView();
        try
        {
            updateOperation = CheckAppUpdatesCoreAsync(manual, download);
            await updateOperation;
        }
        catch (OperationCanceledException) when (updatesStopping.IsCancellationRequested) { }
        catch (Exception error) { if (!quitting) appUpdates.ReportFailure(error); }
        finally { checkingAppUpdate = false; if (!quitting) UpdateAppUpdatesView(); }
    }

    private async Task CheckAppUpdatesCoreAsync(bool manual, bool download)
    {
        if (channelConflictPending) return;
        if (automaticUpdateChecks)
        {
            scheduledAppUpdate = await Task.Run(PendingAppUpdate.Read, updatesStopping.Token);
            if (scheduledAppUpdate?.When is "now" or "applying") { UpdateAppUpdatesView(); return; }
        }
        if (!manual && !appUpdates!.CheckDue) return;
        await appUpdates!.InitializeAsync(updatesStopping.Token);
        if (!manual && (!settingsReady || !AppUpdatePolicy.Enabled(settings))) return;
        if (manual || appUpdates.CheckDue)
        {
            if (AppUpdatePolicy.ThroughProxy(settings) && (engine?.Running != true || !engine.HasProbeProfile))
            {
                if (manual)
                {
                    UpdateStatusText.Text = Ui.T("Connect MegaProxy to check or download updates through the proxy.");
                    WriteLog("Application update deferred: the selected proxy transport is unavailable.");
                }
                return;
            }
            using var transport = AppUpdatePolicy.ThroughProxy(settings) ? engine!.CreateProbeClient(true) : null;
            await appUpdates.CheckAsync(updatesStopping.Token, download, transport);
        }
        if (quitting || channelConflictPending || appUpdates.Package is null) return;
        bool visible = IsVisible && Sections.SelectedItem == SettingsTab && settingsPage == "updates";
        if (visible) await appUpdates.MarkNotificationAsync(true, updatesStopping.Token);
        else if ((manual || AppUpdatePolicy.Enabled(settings)) && appUpdates.NotificationDue)
        {
            updateNoticeVersion = appUpdates.Package!.Version;
            tray.ShowBalloonTip(10000, "MegaProxy", Ui.F("Update {0} is available. Click to open updates.", updateNoticeVersion), Forms.ToolTipIcon.Info);
            await appUpdates.MarkNotificationAsync(false, updatesStopping.Token);
        }
    }

    private async void UpdateNotificationClicked(object? sender, EventArgs e)
    {
        if (!quitting && channelConflictPending) { ShowWindow(); return; }
        if (quitting || updateNoticeVersion is null) return;
        try { await TrackFileAsync(OpenUpdateNotificationAsync()); }
        catch (OperationCanceledException) when (updatesStopping.IsCancellationRequested) { }
        catch (Exception error) { if (!quitting) ShowError(error); }
    }

    private async Task OpenUpdateNotificationAsync()
    {
        ShowWindow();
        Sections.SelectedItem = SettingsTab;
        await NavigateSettingsAsync("updates");
        if (appUpdates is not null) await appUpdates.MarkNotificationAsync(true, updatesStopping.Token);
    }

    private async void InstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if ((appUpdates?.Ready != true && scheduledAppUpdate is null) || AppIdentity.IsDev || quitting || installingAppUpdate) return;
        installingAppUpdate = true; SetBusy(busy);
        try
        {
            string? action = chooseAppUpdate();
            if (action is null) return;
            if (canEdit)
            {
                await FlushSettingsFormAsync();
                await FlushSettingsAsync();
            }
            await installAppUpdate(action);
            // Setup must replace our loaded DLLs; never offer a second launch after handoff.
            if (!quitting && action is "now" or "restart") await QuitAsync();
            else if (automaticUpdateChecks) scheduledAppUpdate = await Task.Run(PendingAppUpdate.Read);
        }
        catch (Exception error) { if (!quitting) ShowError(error); }
        finally { installingAppUpdate = false; if (!quitting) SetBusy(busy); }
    }

}
