using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class RemoteEngine(ServiceClient client, Func<SshHostKeyUpdate, Task<bool>> confirmKey) : IEngine
{
    private ServiceStatus status = new(false, null, null, false, false, "direct", false);
    private ProbeAccess? probe;
    private string? lease;
    private bool borrowed;
    public bool Persistent => true;
    public bool Running => status.Running;
    public bool Recovering => status.Recovering;
    public long Generation => status.Generation;
    public string? InstanceId { get; private set; }
    public Settings? AppliedSettings => status.AppliedSettings;
    public string? AppliedSignature => status.AppliedSignature;
    public bool Http3Selected => status.Http3Selected;
    public bool UsedHttp3Fallback => status.UsedHttp3Fallback;
    public string SelectedOutbound => status.SelectedOutbound;
    public bool HasProbeProfile => status.HasProbeProfile;
    // Polling reconciles remote state; observing a stop must not issue another stop.
    public event Action? Exited { add { } remove { } }
    public event Action<string>? Log;

    public void Apply(ServiceReply reply)
    {
        if (reply.Status is not null) status = reply.Status with { AppliedSettings = reply.Status.AppliedSettings ?? (reply.InstanceId == InstanceId && reply.Status.Running ? status.AppliedSettings : null) };
        InstanceId = reply.InstanceId;
        probe = reply.Probe;
    }

    public void Invalidate()
    {
        status = status with { Running = false, Recovering = false, AppliedSettings = null };
        probe = null;
    }

    public async Task StartAsync(Settings settings, bool testOnly = false, CancellationToken cancellationToken = default)
    {
        ServiceReply reply;
        string instance = client.InstanceId ?? (await client.CallAsync(new("hello"), cancellationToken).ConfigureAwait(false)).InstanceId!;
        try { reply = await client.CallAsync(new("start", TestOnly: testOnly, InstanceId: instance), cancellationToken).ConfigureAwait(false); }
        catch (ServiceHostKeyException key)
        {
            if (!await confirmKey(key.Update).ConfigureAwait(false)) throw new OperationCanceledException("SSH key change was not accepted.");
            await client.CallAsync(new("acceptHostKey", HostKey: key.Update, InstanceId: instance), cancellationToken).ConfigureAwait(false);
            reply = await client.CallAsync(new("start", TestOnly: testOnly, InstanceId: instance), cancellationToken).ConfigureAwait(false);
        }
        lease = reply.Lease;
        borrowed = testOnly && lease is null;
        Apply(reply);
        Log?.Invoke(testOnly ? "Service connection test started." : "Service TUN connected.");
    }

    public async Task StopAsync()
    {
        if (borrowed || !Running && !Recovering || InstanceId is null) return;
        try { Apply(await client.CallAsync(new("stop", Lease: lease, InstanceId: InstanceId, Generation: Generation)).ConfigureAwait(false)); }
        catch (ServiceStateChangedException) { Invalidate(); }
        finally { lease = null; }
    }

    public async Task<ConnectionSnapshot> GetConnectionsAsync(bool showAll)
    {
        var reply = await client.CallAsync(new("connections", ShowAll: showAll, Lease: lease, InstanceId: InstanceId, Generation: Generation)).ConfigureAwait(false);
        Apply(reply);
        var snapshot = reply.Connections ?? new([], 0, 0, 0);
        return snapshot with { Rows = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(snapshot.Rows, row =>
            row.RouteGroup == 1 || row.Route == "System"
                ? new ConnectionRow(row.Process, row.Destination, row.Network, Ui.T(row.Route), row.Upload, row.Download,
                    row.RouteGroup, row.UploadedBytes, row.DownloadedBytes, row.Id) : row)) };
    }

    public HttpClient CreateProbeClient(bool throughProfile)
    {
        var handler = new HttpClientHandler { UseProxy = throughProfile, AllowAutoRedirect = false, UseCookies = false };
        if (throughProfile)
        {
            var access = probe ?? throw new UnauthorizedAccessException("Connection tests require administrator access.");
            handler.Proxy = new WebProxy($"socks5://127.0.0.1:{access.Port}") { Credentials = new NetworkCredential("diagnostic", access.Secret) };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<string> CheckHttp3Async(CancellationToken token) =>
        (await client.CallAsync(new("http3", Lease: lease, InstanceId: InstanceId, Generation: Generation), token).ConfigureAwait(false)).Text ?? "";
    public void Dispose() { }
}
