using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed partial class Engine
{
    private sealed class ProxyTransportException() : IOException("The proxy transport is unavailable.");
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly Channel<bool> failures = Channel.CreateBounded<bool>(1);
    private readonly object evidence = new();
    private DateTime failureWindow;
    private DateTime lastFailureCheck;
    private int failureCount;
    private volatile bool changingProfile, watchingFailures;
    private CancellationTokenSource? failoverStopping;
    private Task failoverTask = Task.CompletedTask;
    public event Action? RuntimeChanged;

    public async Task StartAsync(Settings settings, bool testOnly = false, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Running) return;
            var requested = settings.Copy();
            FailoverPolicy.Validate(requested);
            bool enabled = !testOnly && Configuration.UsesProxy(requested) && FailoverPolicy.Mode(requested) != "DISABLED";
            var selected = requested.Copy();
            var attempted = new HashSet<string>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (enabled) await CheckCandidateAsync(selected, cancellationToken).ConfigureAwait(false);
                    await StartSelectedAsync(selected, testOnly, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (ProxyTransportException) when (enabled)
                {
                    if (!await DirectAvailableAsync(cancellationToken).ConfigureAwait(false)) throw new CoreTransportException("Network unavailable; failover was not attempted.");
                    attempted.Add(selected.ActiveProfileId!);
                    var next = FailoverPolicy.Candidates(requested, selected.ActiveProfileId, attempted).FirstOrDefault();
                    if (next is null) throw new CoreTransportException("No reachable failover profile is available.");
                    selected.ActiveProfileId = next.Id;
                    Log?.Invoke("Proxy transport unavailable; trying the next failover profile.");
                }
            }
            PreservePinnedKey(requested);
            AppliedSignature = Configuration.Signature(requested);
            if (enabled)
            {
                while (failures.Reader.TryRead(out _)) { }
                failureCount = 0;
                lastFailureCheck = default;
                watchingFailures = true;
                failoverStopping = new CancellationTokenSource();
                failoverTask = MonitorFailoverAsync(requested, attempted, failoverStopping.Token);
            }
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        watchingFailures = false;
        failoverStopping?.Cancel();
        try { await failoverTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log?.Invoke("Failover worker failed: " + error.GetType().Name + "; " + error.Message); }
        failoverStopping?.Dispose(); failoverStopping = null; failoverTask = Task.CompletedTask;
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { changingProfile = true; await StopSelectedAsync().ConfigureAwait(false); }
        finally { changingProfile = false; lifecycle.Release(); }
    }

    private void ObserveProxyFailure(string line)
    {
        if (!watchingFailures || changingProfile || !(line.Contains("[proxy]", StringComparison.Ordinal) || line.Contains("connection: open outbound connection:", StringComparison.Ordinal)) || !FailoverPolicy.TransportFailure(line)) return;
        lock (evidence)
        {
            if (DateTime.UtcNow - failureWindow > TimeSpan.FromSeconds(30)) { failureWindow = DateTime.UtcNow; failureCount = 0; }
            if (++failureCount >= 3) { failureCount = 0; failures.Writer.TryWrite(true); }
        }
    }

    private async Task CheckCandidateAsync(Settings settings, CancellationToken token)
    {
        // First-use SSH key verification stays in StartSelectedAsync; never send credentials before it.
        if (settings.ActiveProfile is { Type: "SSH" } ssh && SshConfiguration.PublicKey(ssh).Length == 0) return;
        var runtime = settings.Copy();
        var config = Configuration.Build(runtime, 1, "failover");
        config["inbounds"] = new JsonArray(); config.Remove("experimental");
        config["route"]!["rules"] = new JsonArray(); config["route"]!.AsObject().Remove("rule_set");
        config["route"]!["final"] = "proxy";
        config["log"] = new JsonObject { ["level"] = "error" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var result = await RunCoreToolAsync(CoreExecutable(Configuration.RequiresAlpha(runtime)), config, http3ProbeUri.ToString(), deadline.Token).ConfigureAwait(false);
            if (result.ExitCode == 0) return;
            if (runtime.ActiveProfile!.PreferHttp3 && Http3Fallback.IsUnavailable([result.Error]))
            {
                runtime.ActiveProfile.Proxy["preferHttp2"] = true;
                runtime.ActiveProfile.Proxy["preferHttp3"] = false;
                await CheckCandidateAsync(runtime, token).ConfigureAwait(false);
                return;
            }
            if (FailoverPolicy.TransportFailure(result.Error)) throw new ProxyTransportException();
            throw new IOException("Proxy validation failed. Check credentials, certificates and server compatibility.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ProxyTransportException(); }
    }

    private async Task<bool> DirectAvailableAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try { using var response = await client.GetAsync(http3ProbeUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); return true; }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private async Task<bool> ProxyFailedAsync(CancellationToken token)
    {
        using var client = CreateProbeClient(true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { using var response = await client.GetAsync(http3ProbeUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); return false; }
        catch (HttpRequestException error) { return error.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return true; }
    }

    private async Task MonitorFailoverAsync(Settings requested, HashSet<string> attempted, CancellationToken token)
    {
        var retried = new HashSet<string>();
        while (await failures.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (failures.Reader.TryRead(out _)) { }
            if (DateTime.UtcNow - lastFailureCheck < TimeSpan.FromSeconds(30)) continue;
            lastFailureCheck = DateTime.UtcNow;
            await lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!Running || !await ProxyFailedAsync(token).ConfigureAwait(false) || !await DirectAvailableAsync(token).ConfigureAwait(false)) continue;
                var current = AppliedSettings!.Copy();
                var candidates = new List<ProxyProfile>();
                if (retried.Add(current.ActiveProfileId!)) candidates.Add(current.ActiveProfile!);
                attempted.Add(current.ActiveProfileId!);
                candidates.AddRange(FailoverPolicy.Candidates(requested, current.ActiveProfileId, attempted));
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var next = requested.Copy(); next.ActiveProfileId = candidate.Id;
                    try
                    {
                        if (candidate.Type == "SSH" && SshConfiguration.PublicKey(candidate).Length == 0) continue;
                        await CheckCandidateAsync(next, token).ConfigureAwait(false);
                        changingProfile = true;
                        await StopSelectedAsync().ConfigureAwait(false);
                        await StartSelectedAsync(next, false, token).ConfigureAwait(false);
                        PreservePinnedKey(requested);
                        AppliedSignature = Configuration.Signature(requested);
                        Log?.Invoke(candidate.Id == current.ActiveProfileId ? "Proxy recovered after a fresh connection." : "Failover switched the active connection profile.");
                        RuntimeChanged?.Invoke();
                        break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is IOException or HttpRequestException or ArgumentException or InvalidOperationException)
                    {
                        Log?.Invoke("Failover candidate could not be used; credentials and trust settings were preserved.");
                        if (candidate.Id == current.ActiveProfileId && error is not ProxyTransportException) break;
                        attempted.Add(candidate.Id);
                    }
                    finally { changingProfile = false; }
                }
                if (!Running) { RuntimeChanged?.Invoke(); Exited?.Invoke(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException)
            { Log?.Invoke("Failover check failed; retaining the current connection settings."); }
            finally { lifecycle.Release(); }
        }
    }

    private void PreservePinnedKey(Settings requested)
    {
        if (AppliedSettings?.ActiveProfile is not { Type: "SSH" } profile) return;
        int index = requested.Profiles.FindIndex(item => item.Id == profile.Id);
        if (index >= 0) requested.Profiles[index] = profile.Copy();
    }
}
