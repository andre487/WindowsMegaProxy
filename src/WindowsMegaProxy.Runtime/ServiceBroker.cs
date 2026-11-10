using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class ServiceBroker : IAsyncDisposable
{
    private readonly MachineSettingsStore store;
    private readonly Func<IEngine> createEngine;
    private readonly SemaphoreSlim configuration = new(1), network = new(1);
    private readonly SemaphoreSlim subscription = new(1);
    private readonly Dictionary<Task, CancellationTokenSource> reads = new();
    private readonly Func<bool, Task> setAutostart;
    private readonly BackgroundLog log;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    private Func<bool>? leaseOwnerAlive;
    private ClientLifetime? ownerLifetime;
    private ProbeAccess? probe;
    private Settings settings;
    private long revision;
    private long generation;
    private IEngine? engine;
    private string? lease, leaseOwner;
    private DateTimeOffset leaseDeadline;
    private ServiceStatus status = new(false, null, null, false, false, "direct", false);
    private SshHostKeyUpdate? pendingKey;
    private volatile bool engineChanged;
    private string? settingsError;
    private Settings? desiredSettings;
    private DateTimeOffset nextRecovery, nextHealthCheck;
    private int recoveryFailures, healthFailures;

    public ServiceBroker(MachineSettingsStore store, Func<bool, Task> setAutostart, Func<IEngine>? createEngine = null)
    {
        this.store = store;
        this.setAutostart = setAutostart;
        try { settings = store.Load(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or CryptographicException or FormatException or InvalidOperationException or ArgumentException)
        {
            settings = new Settings { Language = "en" };
            settingsError = "Global settings could not be loaded; connection is disabled until an administrator saves repaired settings. " + error.GetType().Name + "; " + error.Message;
        }
        log = new BackgroundLog(new DiagnosticLog(Path.Combine(store.DirectoryPath, "logs"), settings.LogSizeMb));
        if (settingsError is not null) log.Write(settingsError);
        if (settingsError is null && settings.AutoConnect) desiredSettings = settings.Copy();
        this.createEngine = createEngine ?? (() => new Engine(Path.Combine(store.DirectoryPath, "runtime"), allowEnvironmentOverrides: false));
    }

    public async Task<ServiceReply> HandleAsync(ClientAccess peer, ServiceRequest request, CancellationToken token)
    {
        if (request.Operation == "hello" || request.Operation == "status")
        {
            await configuration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var reply = Reply(peer, request.Operation == "hello", request.Operation == "hello");
                if (lease is not null) return reply with { Status = new(false, null, null, false, false, "direct", false), Probe = null };
                return reply;
            }
            finally { configuration.Release(); }
        }
        if (!peer.CanEdit) return new(false, Error: "Read-only mode");
        if (request.InstanceId != InstanceId) throw new ServiceStateChangedException("The service restarted. Refresh and try again.");
        if (request.Operation is "connections" or "http3")
            return await ReadEngineAsync(peer, request, token).ConfigureAwait(false);
        switch (request.Operation)
        {
            case "refreshSubscription":
                await RefreshSubscriptionAsync(true, token).ConfigureAwait(false);
                return Reply(peer, true);
            case "save":
                if (request.Settings is null) throw new IOException("Missing settings.");
                await configuration.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (request.Revision != revision)
                        throw new IOException("Global settings changed in another window. Review the latest settings and try again.");
                    var next = PortableConfiguration.Copy(request.Settings);
                    PortableConfiguration.ValidateSettings(next);
                    bool changed = settings.LaunchAtSignIn != next.LaunchAtSignIn;
                    if (changed) await setAutostart(next.LaunchAtSignIn == true).ConfigureAwait(false);
                    try
                    {
                        if (settingsError is not null) store.PreserveUnreadableSettings();
                        store.Save(next);
                        settingsError = null;
                    }
                    catch { if (changed) await setAutostart(settings.LaunchAtSignIn == true).ConfigureAwait(false); throw; }
                    settings = next;
                    Interlocked.Increment(ref revision);
                    log.Write("Global settings saved.");
                    await log.SetLimitAsync(next.LogSizeMb).ConfigureAwait(false);
                    return Reply(peer, true);
                }
                finally { configuration.Release(); }
            case "acceptHostKey":
                var key = pendingKey;
                if (key is null || key != request.HostKey) throw new IOException("The pending SSH server key changed. Connect again.");
                await SaveKeyAsync(key, token).ConfigureAwait(false);
                pendingKey = null;
                return Reply(peer, true);
            case "start":
            case "stop":
                await network.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await ExpireLeaseAsync().ConfigureAwait(false);
                    if (request.Operation == "start")
                        return await StartConnectionAsync(peer, request, settings.Copy(), token).ConfigureAwait(false);
                    if (request.Generation != status.Generation)
                        throw new ServiceStateChangedException("The connection changed. Refresh and try again.");
                    if (request.Lease != lease || lease is not null && peer.Sid != leaseOwner)
                        throw new ServiceStateChangedException("The connection test changed. Refresh and try again.");
                    if (lease is not null) leaseDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
                    desiredSettings = null;
                    await StopAsync().ConfigureAwait(false);
                    return Reply(peer);
                }
                finally { network.Release(); }
            case "log": return Reply(peer, includeAppliedSettings: false) with { Text = await log.ReadAsync(tailOnly: true).ConfigureAwait(false) };
            default: throw new IOException("Unknown service operation.");
        }
    }

    private async Task<ServiceReply> ReadEngineAsync(ClientAccess peer, ServiceRequest request, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ServiceReply> read;
        await network.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await ExpireLeaseAsync().ConfigureAwait(false);
            if (request.Generation != status.Generation || request.Lease != lease || lease is not null && peer.Sid != leaseOwner)
                throw new ServiceStateChangedException("The connection changed. Refresh and try again.");
            if (lease is not null) leaseDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
            var reply = Reply(peer, includeAppliedSettings: false);
            if (engine?.Running != true)
            {
                if (request.Operation == "connections") return reply with { Connections = new([], 0, 0, 0) };
                throw new IOException("The proxy is disconnected.");
            }
            read = ReadAsync(engine, reply);
            reads.Add(read, cancellation);
        }
        finally { network.Release(); }
        ServiceReply result;
        try { result = await read.ConfigureAwait(false); }
        finally
        {
            await network.WaitAsync().ConfigureAwait(false);
            try { reads.Remove(read); }
            finally { network.Release(); }
        }
        cancellation.Token.ThrowIfCancellationRequested();
        return result;

        async Task<ServiceReply> ReadAsync(IEngine current, ServiceReply reply) => request.Operation == "connections"
            ? reply with { Connections = await current.GetConnectionsAsync(request.ShowAll, cancellation.Token).ConfigureAwait(false) }
            : reply with { Text = await current.CheckHttp3Async(cancellation.Token).ConfigureAwait(false) };
    }

    private async Task<ServiceReply> StartConnectionAsync(ClientAccess peer, ServiceRequest request, Settings requested, CancellationToken token, bool recovering = false)
    {
        if (settingsError is not null) throw new IOException(settingsError);
        if (engine?.Running == true)
        {
            if (lease is not null) throw new IOException("A connection test is in progress.");
            return Reply(peer);
        }
        if (!request.TestOnly) { desiredSettings = requested.Copy(); if (!recovering) recoveryFailures = 0; }
        if (engine is not null) await StopAsync().ConfigureAwait(false);
        engine = createEngine();
        var current = engine;
        current.Exited += () =>
        {
            if (!ReferenceEquals(engine, current)) return;
            status = status with { Running = false };
            log.Write("Network core exited; persistent connections will be recovered automatically.");
        };
        engine.Log += message => log.Write(message);
        if (engine is Engine native)
        {
            native.RuntimeChanged += () => engineChanged = true;
            native.SaveSshHostKey = update => SaveKeyAsync(update, CancellationToken.None);
            native.ConfirmSshHostKeyChange = update =>
            {
                pendingKey = update;
                throw new ServiceHostKeyException(update);
            };
        }
        try
        {
            if (request.TestOnly)
            {
                var owner = ownerLifetime = peer.Watch?.Invoke();
                leaseOwnerAlive = owner is null ? peer.IsAlive : () => owner.IsAlive;
                if (leaseOwnerAlive?.Invoke() == false) throw new IOException("The diagnostic client exited.");
            }
            await engine.StartAsync(requested.Copy(), request.TestOnly, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            string detail = error is System.ComponentModel.Win32Exception windowsError ? $"Windows error {windowsError.NativeErrorCode}." : error.Message;
            log.Write($"Service connection start failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}; {detail}");
            if (!request.TestOnly) ScheduleRecovery(error, token);
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        if (request.TestOnly)
        {
            lease = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            leaseOwner = peer.Sid;
            leaseDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
        }
        UpdateStatus();
        if (!request.TestOnly)
        {
            desiredSettings = engine.AppliedSettings?.Copy() ?? requested.Copy();
            healthFailures = 0;
            if (!recovering) nextRecovery = DateTimeOffset.UtcNow.AddSeconds(5);
        }
        probe = (engine as Engine)?.ProbeAccess;
        log.Write(request.TestOnly ? "Service connection test started." : "Service TUN connected.");
        return Reply(peer) with { Lease = lease };
    }

    private ServiceReply Reply(ClientAccess peer, bool includeSettings = false, bool includeAppliedSettings = true)
    {
        var current = status with { Recovering = !status.Running && desiredSettings is not null && lease is null };
        if (!includeAppliedSettings) current = current with { AppliedSettings = null };
        // Test proxy credentials belong only to the administrator who owns its lease.
        bool visible = lease is null || leaseOwner == peer.Sid;
        return new(peer.CanEdit,
            Settings: includeSettings ? peer.CanEdit ? PortableConfiguration.Copy(settings) : ServiceView.PublicSettings(settings) : null,
            Status: visible ? peer.CanEdit ? current : ServiceView.PublicStatus(current) : new(false, null, null, false, false, "direct", false),
            Probe: peer.CanEdit && visible && current.Running ? probe : null,
            Initialized: store.Initialized, AutoStart: settings.LaunchAtSignIn == true, Revision: Interlocked.Read(ref revision),
            InstanceId: InstanceId, Testing: lease is not null,
            Text: settingsError is null ? null : ServiceProtocol.ConfigurationRecoveryMessage);
    }

    private async Task SaveKeyAsync(SshHostKeyUpdate update, CancellationToken token)
    {
        await configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = PortableConfiguration.Copy(settings);
            var profile = next.Profiles.FirstOrDefault(item => item.Id == update.ProfileId);
            if (!SshConfiguration.ApplyKey(profile, update))
                throw new IOException("The SSH profile changed during key verification. Connect again.");
            store.Save(next);
            settings = next;
            Interlocked.Increment(ref revision);
        }
        finally { configuration.Release(); }
    }

    private void UpdateStatus() => status = new(engine?.Running == true, engine?.AppliedSettings, engine?.AppliedSignature,
        engine?.Http3Selected == true, engine?.UsedHttp3Fallback == true, engine?.SelectedOutbound ?? "direct", engine?.HasProbeProfile == true, Interlocked.Increment(ref generation));

    private async Task StopAsync()
    {
        // Cancel and drain readers before disposing the core; they never hold the network gate while awaiting I/O.
        foreach (var cancellation in reads.Values) cancellation.Cancel();
        try { await Task.WhenAll(reads.Keys).ConfigureAwait(false); }
        catch (Exception) { /* Each reader observes its own failure. */ }
        bool running = status.Running;
        status = status with { Running = false };
        probe = null;
        try { if (engine is not null) await engine.StopAsync().ConfigureAwait(false); }
        finally { engine?.Dispose(); engine = null; lease = leaseOwner = null; leaseOwnerAlive = null; ownerLifetime?.Dispose(); ownerLifetime = null; UpdateStatus(); if (running) log.Write("Service core stopped."); }
    }

    public async Task ExportLogAsync(ClientAccess peer, Stream output, CancellationToken token)
    {
        if (!peer.CanEdit) throw new UnauthorizedAccessException("Read-only mode");
        string snapshot = Path.Combine(store.DirectoryPath, "runtime", "log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await log.ExportAsync(snapshot).ConfigureAwait(false);
            await using var input = File.OpenRead(snapshot);
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                await ServiceProtocol.WriteAsync(output, new ServiceLogChunk(buffer[..count]), token).ConfigureAwait(false);
            await ServiceProtocol.WriteAsync(output, new ServiceLogChunk([], Done: true), token).ConfigureAwait(false);
        }
        finally { File.Delete(snapshot); }
    }

    private Task ExpireLeaseAsync() => lease is not null && (DateTimeOffset.UtcNow >= leaseDeadline || leaseOwnerAlive?.Invoke() == false) ? StopAsync() : Task.CompletedTask;

    private async Task RefreshSubscriptionAsync(bool manual, CancellationToken token)
    {
        if (!await subscription.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            Settings snapshot;
            long expected;
            await configuration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (settingsError is not null || !manual && !ConfigurationSubscription.Due(settings, DateTimeOffset.UtcNow)) return;
                snapshot = settings.Copy(); expected = revision;
            }
            finally { configuration.Release(); }
            var updated = await SubscriptionRefresh.RunAsync(snapshot, manual, token).ConfigureAwait(false);
            if (updated is null) return;
            await configuration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (expected != revision) return;
                store.Save(updated);
                settings = updated;
                Interlocked.Increment(ref revision);
                log.Write("Configuration subscription: " + ConfigurationSubscription.Status(updated) + ".");
                await log.SetLimitAsync(updated.LogSizeMb).ConfigureAwait(false);
            }
            finally { configuration.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { log.Write("Configuration subscription update could not be saved."); }
        finally { subscription.Release(); }
    }

    public async Task MaintainAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Task refresh = Task.CompletedTask;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (refresh.IsCompleted)
                {
                    try { await refresh.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { log.Write("Subscription worker failed: " + error.GetType().Name + "; " + error.Message); }
                    refresh = Task.Run(() => RefreshSubscriptionAsync(false, token), token);
                }
                if (!await network.WaitAsync(0, token).ConfigureAwait(false)) continue;
                try
                {
                    await ExpireLeaseAsync().ConfigureAwait(false);
                    if (engineChanged) { engineChanged = false; UpdateStatus(); probe = (engine as Engine)?.ProbeAccess; }
                    await RecoverAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException or TimeoutException)
                { log.Write("Service maintenance failed; the next cycle will retry: " + error.GetType().Name + "; " + error.Message); }
                finally { network.Release(); }
            }
        }
        finally { try { await refresh.ConfigureAwait(false); } catch (OperationCanceledException) when (token.IsCancellationRequested) { } }
    }

    private void ScheduleRecovery(Exception error, CancellationToken token)
    {
        bool transient = error is TimeoutException or System.Net.Sockets.SocketException or CoreTransportException ||
            error is System.Net.Http.HttpRequestException http && http.HttpRequestError is
                System.Net.Http.HttpRequestError.ConnectionError or System.Net.Http.HttpRequestError.NameResolutionError;
        if (token.IsCancellationRequested || !transient)
        {
            desiredSettings = null;
            log.Write("Automatic connection recovery paused; user action is required.");
            return;
        }
        DelayRecovery();
    }

    private void DelayRecovery(bool failed = true)
    {
        int seconds = 5 << recoveryFailures;
        recoveryFailures = Math.Min(recoveryFailures + 1, 4);
        seconds = Math.Min(seconds, 60);
        nextRecovery = DateTimeOffset.UtcNow.AddSeconds(seconds);
        log.Write(failed ? $"Connection recovery scheduled in {seconds} seconds." : "Service connection recovered.");
    }

    private async Task RecoverAsync(CancellationToken token)
    {
        if (desiredSettings is null || lease is not null) return;
        if (engine?.Running == true)
        {
            if (DateTimeOffset.UtcNow < nextHealthCheck) return;
            nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(5);
            if (await engine.IsHealthyAsync(token).ConfigureAwait(false))
            {
                healthFailures = 0;
                if (DateTimeOffset.UtcNow >= nextRecovery.AddMinutes(1)) recoveryFailures = 0;
                return;
            }
            if (++healthFailures < 3) return;
            log.Write("Network core control API failed three consecutive health checks; restarting the core.");
            await StopAsync().ConfigureAwait(false);
        }
        if (DateTimeOffset.UtcNow < nextRecovery) return;
        var requested = desiredSettings.Copy();
        await StopAsync().ConfigureAwait(false);
        try
        {
            await StartConnectionAsync(new("S-1-5-18", true), new("start", InstanceId: InstanceId), requested, token, recovering: true).ConfigureAwait(false);
            DelayRecovery(failed: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { log.Write("Automatic connection recovery failed: " + error.GetType().Name + "; " + error.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        desiredSettings = null;
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            try { await log.DisposeAsync().ConfigureAwait(false); }
            finally { configuration.Dispose(); network.Dispose(); subscription.Dispose(); }
        }
    }
}
