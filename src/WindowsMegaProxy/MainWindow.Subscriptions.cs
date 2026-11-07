using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private readonly CancellationTokenSource subscriptionStopping = new();
    private bool subscriptionRefreshing;

    private void FailoverClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit || busy || quitting) return;
        new FailoverWindow(settings.Copy(), async (mode, ids) =>
        {
            var updated = CaptureSettings();
            var options = updated.SharedOptions["failover"]?.DeepClone() as System.Text.Json.Nodes.JsonObject ?? new();
            options["mode"] = mode;
            options["profileIds"] = new System.Text.Json.Nodes.JsonArray(Array.ConvertAll(ids, id => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(id)!));
            updated.SharedOptions["failover"] = options;
            await CommitSettingsAsync(updated);
        }) { Owner = this }.ShowDialog();
    }

    private void SubscriptionClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit || busy || quitting) return;
        var window = new SubscriptionWindow(settings.Copy(), async definition =>
        {
            var updated = CaptureSettings();
            ConfigurationSubscription.Configure(updated, definition);
            await CommitSettingsAsync(updated);
        }, async () =>
        {
            await RefreshSubscriptionAsync(true);
            return settings.Copy();
        }) { Owner = this };
        window.ShowDialog();
    }

    private async Task RefreshSubscriptionAsync(bool manual = false)
    {
        if (!canEdit || settingsLoading || quitting || subscriptionRefreshing || !settingsReady || busy || service is not null && !manual) return;
        if (!manual && !ConfigurationSubscription.Due(settings, DateTimeOffset.UtcNow)) return;
        subscriptionRefreshing = true;
        try
        {
            await FlushSettingsAsync();
            long expected = settingsRevision;
            var snapshot = CaptureSettings();
            if (service is not null)
            {
                var reply = await service.CallAsync(new("refreshSubscription"), subscriptionStopping.Token);
                if (!quitting && expected == settingsRevision && reply.Settings is { } updated)
                {
                    Interlocked.Exchange(ref serviceRevision, reply.Revision);
                    ApplySettings(updated);
                    await analysisTask;
                }
            }
            else
            {
                var updated = await TrackFileAsync(Task.Run(() => SubscriptionRefresh.RunAsync(snapshot, manual, subscriptionStopping.Token), subscriptionStopping.Token));
                if (updated is not null && !quitting && expected == settingsRevision)
                {
                    await CommitSettingsAsync(updated);
                    WriteLog("Configuration subscription: " + ConfigurationSubscription.Status(updated) + ".");
                }
            }
        }
        catch (OperationCanceledException) when (subscriptionStopping.IsCancellationRequested) { }
        catch (Exception) { if (manual) throw; WriteLog("Configuration subscription update failed."); }
        finally { subscriptionRefreshing = false; }
    }
}
