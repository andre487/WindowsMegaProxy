using System.Threading.Tasks;
using System.Windows;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private string serviceState = "Service: checking…";
    private bool queryingServiceState;

    private bool ServiceUpdateInProgress => scheduledAppUpdate?.When is "now" or "applying";
    private string ServiceConnectionMessage => ServiceUpdateInProgress ? Ui.T("Update in progress") : ServiceUnavailableMessage;

    private async Task RefreshInstallationStateAsync()
    {
        if (!automaticUpdateChecks) return;
        scheduledAppUpdate = await TrackFileAsync(Task.Run(PendingAppUpdate.Read));
        if (!quitting) UpdateAppUpdatesView();
    }

    private void UpdateServiceStateView()
    {
        ServiceStateText.Visibility = service is null ? Visibility.Collapsed : Visibility.Visible;
        ServiceStateText.Text = Ui.T(ServiceUpdateInProgress ? "Update in progress" : serviceState == "Service: running" && !serviceAvailable
            ? "Service: running, client disconnected" : serviceState);
        var warnings = PodkopCatalog.Warnings(engine?.AppliedSettings);
        RoutingWarningText.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RoutingWarningText.Text = warnings.Length == 0 ? "" : Ui.F("Unavailable destination lists: {0}. See the log.", string.Join(", ", warnings));
    }

    private async Task RefreshServiceStateAsync()
    {
        if (service is null || queryingServiceState || quitting) return;
        queryingServiceState = true;
        try { serviceState = await TrackFileAsync(Task.Run(StartupConfiguration.QueryServiceState)); if (!quitting) UpdateServiceStateView(); }
        finally { queryingServiceState = false; }
    }
}
