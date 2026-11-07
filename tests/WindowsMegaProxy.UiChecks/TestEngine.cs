using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal sealed class TestEngine : IEngine
{
    public bool Running { get; set; }
    public Settings? AppliedSettings { get; set; }
    public string? AppliedSignature { get; set; }
    public bool Http3Selected { get; set; }
    public bool UsedHttp3Fallback { get; set; }
    public string SelectedOutbound => HasProbeProfile ? "proxy" : "direct";
    public bool HasProbeProfile => AppliedSettings is { } settings && Configuration.UsesProxy(settings);
    public bool Disposed { get; private set; }
    public int Stops { get; private set; }
    public int Polls { get; private set; }
    public int PollThread { get; private set; }
    public Func<CancellationToken, Task>? Start { get; set; }
    public event Action? Exited;
    public event Action<string>? Log;
    public async Task StartAsync(Settings settings, bool testOnly = false, CancellationToken cancellationToken = default)
    {
        AppliedSettings = PortableConfiguration.Copy(settings);
        AppliedSignature = Configuration.Signature(settings);
        if (Start is not null) await Start(cancellationToken);
        Http3Selected = Configuration.RequiresHttp3(settings);
        Running = true;
        Log?.Invoke("Test engine started.");
    }
    public Task StopAsync() { Stops++; Running = false; return Task.CompletedTask; }
    public void Exit() { Running = false; Exited?.Invoke(); }
    public void Dispose() => Disposed = true;
    public Task<ConnectionSnapshot> GetConnectionsAsync(bool showAll)
    {
        Polls++; PollThread = Environment.CurrentManagedThreadId;
        return Task.FromResult(new ConnectionSnapshot([], 0, 0, 0));
    }
    public HttpClient CreateProbeClient(bool throughProfile) => throw new InvalidOperationException("UI fixtures must not send network traffic.");
    public Task<string> CheckHttp3Async(CancellationToken token) => Task.FromResult("");
}
