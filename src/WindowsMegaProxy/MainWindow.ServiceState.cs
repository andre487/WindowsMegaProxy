using System.Threading.Tasks;
using System.Windows;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private string serviceState = "Service: checking…";
    private bool queryingServiceState;

    private void UpdateServiceStateView()
    {
        ServiceStateText.Visibility = service is null ? Visibility.Collapsed : Visibility.Visible;
        ServiceStateText.Text = Ui.T(serviceState == "Service: running" && !serviceAvailable
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
