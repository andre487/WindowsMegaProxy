using System;
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

    public ServiceBroker(MachineSettingsStore store, Func<bool, Task> setAutostart, Func<IEngine>? createEngine = null)
    {
        this.store = store;
        this.setAutostart = setAutostart;
        settings = store.Load();
        log = new BackgroundLog(new DiagnosticLog(Path.Combine(store.DirectoryPath, "logs"), settings.LogSizeMb));
        this.createEngine = createEngine ?? (() => new Engine(Path.Combine(store.DirectoryPath, "runtime"), allowEnvironmentOverrides: false));
    }

    public bool AutoConnect => !AppIdentity.IsDev && settings.AutoConnect;

    public async Task<ServiceReply> HandleAsync(ClientAccess peer, ServiceRequest request, CancellationToken token)
    {
        if (request.Operation == "hello" || request.Operation == "status")
        {
            await configuration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var reply = Reply(peer, request.Operation == "hello");
                if (lease is not null) return reply with { Status = new(false, null, null, false, false, "direct", false), Probe = null };
                return request.Operation == "status" && reply.Status is not null ? reply with { Status = reply.Status with { AppliedSettings = null } } : reply;
            }
            finally { configuration.Release(); }
        }
        if (!peer.CanEdit) return new(false, Error: "Read-only mode");
        if (request.InstanceId != InstanceId) throw new ServiceStateChangedException("The service restarted. Refresh and try again.");
        switch (request.Operation)
        {
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
                    try { store.Save(next); }
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
            case "connections":
            case "http3":
                await network.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await ExpireLeaseAsync().ConfigureAwait(false);
                    if (request.Operation == "start")
                    {
                        if (engine?.Running == true)
                        {
                            if (lease is not null) throw new IOException("A connection test is in progress.");
                            return Reply(peer);
                        }
                        engine?.Dispose();
                        engine = createEngine();
                        var current = engine;
                        current.Exited += () => { if (ReferenceEquals(engine, current)) status = status with { Running = false }; };
                        engine.Log += message => log.Write(message);
                        if (engine is Engine native)
                        {
                            native.SaveSshHostKey = update => SaveKeyAsync(update, token);
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
                            await engine.StartAsync(PortableConfiguration.Copy(settings), request.TestOnly, token).ConfigureAwait(false);
                        }
                        catch { await StopAsync().ConfigureAwait(false); throw; }
                        if (request.TestOnly)
                        {
                            lease = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                            leaseOwner = peer.Sid;
                            leaseDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
                        }
                        UpdateStatus();
                        probe = (engine as Engine)?.ProbeAccess;
                        log.Write(request.TestOnly ? "Service connection test started." : "Service TUN connected.");
                        return Reply(peer) with { Lease = lease };
                    }
                    if (request.Generation != status.Generation)
                        throw new ServiceStateChangedException("The connection changed. Refresh and try again.");
                    if (request.Lease != lease || lease is not null && peer.Sid != leaseOwner)
                        throw new ServiceStateChangedException("The connection test changed. Refresh and try again.");
                    if (lease is not null) leaseDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
                    switch (request.Operation)
                    {
                        case "stop": await StopAsync().ConfigureAwait(false); return Reply(peer);
                        case "connections":
                            return Reply(peer) with { Connections = engine?.Running == true ? await engine.GetConnectionsAsync(request.ShowAll).ConfigureAwait(false) : new([], 0, 0, 0) };
                        default:
                            if (engine?.Running != true) throw new IOException("The proxy is disconnected.");
                            return Reply(peer) with { Text = await engine.CheckHttp3Async(token).ConfigureAwait(false) };
                    }
                }
                finally { network.Release(); }
            case "log": return Reply(peer) with { Text = await log.ReadAsync(tailOnly: true).ConfigureAwait(false) };
            default: throw new IOException("Unknown service operation.");
        }
    }

    private ServiceReply Reply(ClientAccess peer, bool includeSettings = false)
    {
        var current = status;
        // Test proxy credentials belong only to the administrator who owns its lease.
        bool visible = lease is null || leaseOwner == peer.Sid;
        return new(peer.CanEdit,
            Settings: includeSettings ? peer.CanEdit ? PortableConfiguration.Copy(settings) : ServiceView.PublicSettings(settings) : null,
            Status: visible ? peer.CanEdit ? current : ServiceView.PublicStatus(current) : new(false, null, null, false, false, "direct", false),
            Probe: peer.CanEdit && visible && current.Running ? probe : null,
            Initialized: store.Initialized, AutoStart: settings.LaunchAtSignIn == true, Revision: Interlocked.Read(ref revision),
            InstanceId: InstanceId, Testing: lease is not null);
    }

    private async Task SaveKeyAsync(SshHostKeyUpdate update, CancellationToken token)
    {
        await configuration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = PortableConfiguration.Copy(settings);
            var profile = next.Profiles.FirstOrDefault(item => item.Id == update.ProfileId);
            if (profile?.Type != "SSH" || profile.Host != update.Host || profile.Port != update.Port ||
                (profile.Proxy["trustedHostKey"]?.GetValue<string>() ?? "") != update.PreviousFingerprint ||
                SshConfiguration.Fingerprint(update.PublicKey) != update.Fingerprint)
                throw new IOException("The SSH profile changed during key verification. Connect again.");
            if (profile.Data["windows"] is not JsonObject) profile.Data["windows"] = new JsonObject();
            profile.Data["windows"]!["sshHostKey"] = update.PublicKey;
            profile.Proxy["trustedHostKey"] = update.Fingerprint;
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

    public async Task MaintainAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (!await network.WaitAsync(0, token).ConfigureAwait(false)) continue;
            try { await ExpireLeaseAsync().ConfigureAwait(false); }
            finally { network.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await log.DisposeAsync().ConfigureAwait(false);
        configuration.Dispose(); network.Dispose();
    }
}
