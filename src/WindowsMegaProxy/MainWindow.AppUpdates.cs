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
        changingUpdatePreference = false;
        AutomaticUpdatesBox.IsEnabled = canEdit && !quitting && !settingsLoading;
        InstalledVersionText.Text = Ui.F("Installed version: {0}", AppUpdateTrust.CurrentVersion.ToString(3));
        string status = installingAppUpdate ? "Preparing update…" : appUpdates?.StatusKey ?? "Update files are unavailable.";
        if (!installingAppUpdate && scheduledAppUpdate is { } pending)
            status = pending.When is "now" or "applying" ? "Installing update…" : pending.When == "launch" ? "Update scheduled for the next application start." : "Update scheduled for the next computer restart.";
        UpdateStatusText.Text = Ui.T(status);
        UpdateErrorText.Text = appUpdates?.ErrorKey is { } error && error != status ? Ui.T(error) : "";
        UpdateErrorText.Visibility = UpdateErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool ready = appUpdates?.Ready == true || scheduledAppUpdate is not null;
        UpdateVersionText.Text = ready ? Ui.F("Available version: {0}", scheduledAppUpdate?.Version ?? appUpdates!.Package!.Version) : "";
        UpdateVersionText.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = Ui.T(scheduledAppUpdate is null ? "Install update" : "Change update timing");
        InstallUpdateButton.IsEnabled = ready && scheduledAppUpdate?.When is not ("now" or "applying") && canEdit && !AppIdentity.IsDev && !installingAppUpdate && !checkingAppUpdate && !quitting;
        InstallUpdateButton.ToolTip = AppIdentity.IsDev ? Ui.T("Development builds are updated with run.cmd.") : !canEdit ? Ui.T("Administrator rights are required to install updates.") : null;
        CheckUpdatesButton.IsEnabled = scheduledAppUpdate is null && !checkingAppUpdate && !installingAppUpdate && !quitting;
        UpdateDownloadProgress.Visibility = appUpdates?.StatusKey == "Downloading update…" ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadProgress.Value = appUpdates?.Progress ?? 0;
    }

    private async void AutomaticUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (changingUpdatePreference || !canEdit || quitting || settingsLoading) return;
        try
        {
            var updated = CaptureSettings();
            updated.WindowsOptions[AppUpdatePolicy.Option] = AutomaticUpdatesBox.IsChecked == true;
            await CommitSettingsAsync(updated);
        }
        catch (Exception error) { ShowError(error); UpdateAppUpdatesView(); }
    }

    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await RunAppUpdateCheckAsync(manual: true);

    private async Task RunAppUpdateCheckAsync(bool manual)
    {
        if (appUpdates is null || checkingAppUpdate || installingAppUpdate || quitting || !settingsReady || settingsLoading) return;
        if (!manual && !automaticUpdateChecks) return;
        checkingAppUpdate = true;
        UpdateAppUpdatesView();
        try
        {
            updateOperation = CheckAppUpdatesCoreAsync(manual);
            await updateOperation;
        }
        catch (OperationCanceledException) when (updatesStopping.IsCancellationRequested) { }
        catch (Exception error) { if (!quitting) WriteLog("Application update operation failed: " + error.GetType().Name + "."); }
        finally { checkingAppUpdate = false; if (!quitting) UpdateAppUpdatesView(); }
    }

    private async Task CheckAppUpdatesCoreAsync(bool manual)
    {
        if (channelConflictPending) return;
        if (automaticUpdateChecks)
        {
            scheduledAppUpdate = await Task.Run(PendingAppUpdate.Read, updatesStopping.Token);
            if (scheduledAppUpdate is not null) { UpdateAppUpdatesView(); return; }
        }
        await appUpdates!.InitializeAsync(updatesStopping.Token);
        if (!manual && (!canEdit || !AppUpdatePolicy.Enabled(settings))) return;
        if (manual || appUpdates.CheckDue) await appUpdates.CheckAsync(updatesStopping.Token);
        if (quitting || !appUpdates.Ready) return;
        bool visible = IsVisible && Sections.SelectedItem == SettingsTab && settingsPage == "updates";
        if (visible) await appUpdates.MarkNotificationAsync(true, updatesStopping.Token);
        else if ((manual || AppUpdatePolicy.Enabled(settings)) && canEdit && appUpdates.NotificationDue)
        {
            updateNoticeVersion = appUpdates.Package!.Version;
            tray.ShowBalloonTip(10000, "MegaProxy", Ui.F("Update {0} is downloaded. Click to install.", updateNoticeVersion), Forms.ToolTipIcon.Info);
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
        if ((appUpdates?.Ready != true && scheduledAppUpdate is null) || !canEdit || AppIdentity.IsDev || quitting || installingAppUpdate) return;
        installingAppUpdate = true; SetBusy(busy);
        try
        {
            string? action = chooseAppUpdate();
            if (action is null) return;
            await FlushSettingsFormAsync();
            await FlushSettingsAsync();
            await installAppUpdate(action);
            // Setup must replace our loaded DLLs; never offer a second launch after handoff.
            if (!quitting && action is "now" or "restart") await QuitAsync();
            else if (automaticUpdateChecks) scheduledAppUpdate = await Task.Run(PendingAppUpdate.Read);
        }
        catch (Exception error) { if (!quitting) ShowError(error); }
        finally { installingAppUpdate = false; if (!quitting) SetBusy(busy); }
    }

}
