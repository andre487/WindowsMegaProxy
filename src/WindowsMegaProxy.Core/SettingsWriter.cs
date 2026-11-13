using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record SettingsAnalysis(Settings Settings, string? Signature, string? Error)
{
    public static bool SameNetwork(Settings a, Settings b) => a.DefaultAction == b.DefaultAction && a.BypassLan == b.BypassLan &&
        a.BlockDisabledIpv6 == b.BlockDisabledIpv6 && a.SystemAllowIpv6 == b.SystemAllowIpv6 && a.CoreLogLevel == b.CoreLogLevel && a.TunMtu == b.TunMtu && a.TlsFingerprint == b.TlsFingerprint && a.BootstrapDnsProvider == b.BootstrapDnsProvider && a.ActiveProfileId == b.ActiveProfileId &&
        AppUpdatePolicy.ThroughProxy(a) == AppUpdatePolicy.ThroughProxy(b) &&
        SshConfiguration.AuthMode(a) == SshConfiguration.AuthMode(b) &&
        JsonNode.DeepEquals(a.SharedOptions["ssh"]?["fingerprint"], b.SharedOptions["ssh"]?["fingerprint"]) &&
        JsonNode.DeepEquals(a.SharedOptions["failover"], b.SharedOptions["failover"]) &&
        (FailoverPolicy.Mode(a) == "DISABLED" || a.Profiles.Count == b.Profiles.Count && a.Profiles.Zip(b.Profiles).All(pair => pair.First.Id == pair.Second.Id && JsonNode.DeepEquals(pair.First.Proxy, pair.Second.Proxy) && JsonNode.DeepEquals(pair.First.Data["dns"], pair.Second.Data["dns"]))) &&
        a.Rules.Count == b.Rules.Count && a.Rules.Zip(b.Rules).All(pair => pair.First.Source == pair.Second.Source &&
            pair.First.Path == pair.Second.Path && pair.First.Destination == pair.Second.Destination && pair.First.Action == pair.Second.Action && pair.First.Enabled == pair.Second.Enabled && pair.First.PodkopThroughProxy == pair.Second.PodkopThroughProxy) &&
        JsonNode.DeepEquals(a.ActiveProfile?.Proxy, b.ActiveProfile?.Proxy) && JsonNode.DeepEquals(a.ActiveProfile?.Data["dns"], b.ActiveProfile?.Data["dns"]);

    public static SettingsAnalysis Prepare(Settings settings, SettingsAnalysis? previous = null)
    {
        var snapshot = settings.Copy();
        PortableConfiguration.ValidateSettings(snapshot);
        ProfileAppearance.AssignMissingColors(snapshot.Profiles);
        if (previous is not null && SameNetwork(snapshot, previous.Settings) &&
            (previous.Error is null || previous.Settings.Language == snapshot.Language)) return previous with { Settings = snapshot };
        try { return new(snapshot, Configuration.Signature(snapshot), null); }
        catch (Exception error) when (error is ArgumentException or System.IO.InvalidDataException) { return new(snapshot, null, error.Message); }
    }
}

// Owns immutable input snapshots; pending changes are combined without reordering disk writes.
public sealed class SettingsWriter(Action<Settings> write)
{
    private readonly object gate = new();
    private Settings? pending;
    private List<TaskCompletionSource<SettingsAnalysis>> waiters = [];
    private Task worker = Task.CompletedTask;
    private bool running;
    private SettingsAnalysis? last;

    public Task<SettingsAnalysis> SaveAsync(Settings snapshot)
    {
        var completion = new TaskCompletionSource<SettingsAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending = snapshot;
            waiters.Add(completion);
            if (!running) { running = true; worker = Task.Run(DrainAsync); }
        }
        return completion.Task;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            await Task.Delay(50).ConfigureAwait(false);
            Settings snapshot;
            List<TaskCompletionSource<SettingsAnalysis>> batch;
            lock (gate)
            {
                if (pending is null) { running = false; return; }
                snapshot = pending; pending = null;
                batch = waiters; waiters = [];
            }
            try
            {
                var result = SettingsAnalysis.Prepare(snapshot, last);
                write(result.Settings);
                last = result;
                foreach (var waiter in batch) waiter.TrySetResult(result);
            }
            catch (Exception error) { foreach (var waiter in batch) waiter.TrySetException(error); }
        }
    }

    public Task FlushAsync() { lock (gate) return worker; }
}
