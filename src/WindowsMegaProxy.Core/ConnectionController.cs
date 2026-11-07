using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting }

public interface IEngine : IDisposable
{
    bool Persistent => false;
    bool Running { get; }
    Settings? AppliedSettings { get; }
    string? AppliedSignature { get; }
    bool Http3Selected { get; }
    bool UsedHttp3Fallback { get; }
    string SelectedOutbound { get; }
    bool HasProbeProfile { get; }
    event Action? Exited;
    event Action<string>? Log;
    Task StartAsync(Settings settings, bool testOnly = false, CancellationToken cancellationToken = default);
    Task StopAsync();
    Task<ConnectionSnapshot> GetConnectionsAsync(bool showAll);
    HttpClient CreateProbeClient(bool throughProfile);
    Task<string> CheckHttp3Async(CancellationToken token);
}

// Commands enter synchronously on the UI thread; all work is owned until shutdown completes.
public sealed class ConnectionController(Func<IEngine> createEngine)
{
    public IEngine? Engine { get; private set; }
    public ConnectionState State { get; private set; }
    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Disconnecting;
    public bool IsTesting { get; private set; }
    public event Action? Changed;
    public event Action? UnexpectedExit;
    private CancellationTokenSource? connecting, diagnostic;
    private Task connectionTask = Task.CompletedTask, diagnosticTask = Task.CompletedTask, disconnectTask = Task.CompletedTask;
    private bool closing;

    public void Attach(IEngine current)
    {
        if (!ReferenceEquals(Engine, current)) Engine?.Dispose();
        Engine = current;
        State = current.Running ? ConnectionState.Connected : ConnectionState.Disconnected;
        Changed?.Invoke();
    }

    public Task ConnectAsync(Settings settings)
    {
        if (closing || IsBusy || IsTesting || State == ConnectionState.Connected)
            throw new InvalidOperationException("Another connection operation is in progress.");
        var snapshot = PortableConfiguration.Copy(settings);
        connecting = new CancellationTokenSource();
        State = ConnectionState.Connecting;
        Changed?.Invoke();
        return connectionTask = ConnectCoreAsync(snapshot, connecting.Token);
    }

    private async Task ConnectCoreAsync(Settings settings, CancellationToken token)
    {
        try
        {
            if (Engine is not null) { await Task.Run(Engine.StopAsync); Engine.Dispose(); Engine = null; }
            var current = Engine = createEngine();
            current.Exited += () => { if (Engine == current && State == ConnectionState.Connected && !closing) UnexpectedExit?.Invoke(); };
            await Task.Run(() => current.StartAsync(settings, cancellationToken: token), token);
            token.ThrowIfCancellationRequested();
            State = ConnectionState.Connected;
        }
        catch
        {
            try { if (Engine is not null) await Task.Run(Engine.StopAsync); }
            finally { Engine?.Dispose(); Engine = null; if (State != ConnectionState.Disconnecting) State = ConnectionState.Disconnected; }
            throw;
        }
        finally { connecting?.Dispose(); connecting = null; Changed?.Invoke(); }
    }

    public void Cancel() { connecting?.Cancel(); diagnostic?.Cancel(); }

    public Task DisconnectAsync()
    {
        if (State == ConnectionState.Disconnecting) return disconnectTask;
        State = ConnectionState.Disconnecting;
        Changed?.Invoke();
        return disconnectTask = DisconnectCoreAsync();
    }

    private async Task DisconnectCoreAsync()
    {
        Cancel();
        try { await connectionTask; } catch (Exception) { /* Startup's caller reports the failure. */ }
        try
        {
            try { await diagnosticTask; } catch (Exception) { /* The diagnostic caller reports the failure. */ }
            if (Engine is not null) await Task.Run(Engine.StopAsync);
        }
        finally { Engine?.Dispose(); Engine = null; State = ConnectionState.Disconnected; Changed?.Invoke(); }
    }

    public Task<bool> RunDiagnosticsAsync(Settings settings, Func<IEngine, CancellationToken, Task> action)
    {
        if (closing || IsBusy || IsTesting) return Task.FromResult(false);
        var snapshot = PortableConfiguration.Copy(settings);
        IsTesting = true;
        diagnostic = new CancellationTokenSource();
        Changed?.Invoke();
        var task = RunDiagnosticsCoreAsync(snapshot, action, diagnostic.Token);
        diagnosticTask = task;
        return task;
    }

    private async Task<bool> RunDiagnosticsCoreAsync(Settings settings, Func<IEngine, CancellationToken, Task> action, CancellationToken token)
    {
        IEngine? temporary = null;
        try
        {
            var target = Engine;
            if (target?.Running != true)
            {
                target = temporary = createEngine();
                await Task.Run(() => target.StartAsync(settings, testOnly: true, cancellationToken: token), token);
            }
            await action(target, token);
            return true;
        }
        finally
        {
            try { if (temporary is not null) await Task.Run(temporary.StopAsync); }
            finally { temporary?.Dispose(); diagnostic?.Dispose(); diagnostic = null; IsTesting = false; Changed?.Invoke(); }
        }
    }

    public async Task ShutdownAsync()
    {
        closing = true;
        if (Engine?.Persistent == true && !IsBusy)
        {
            diagnostic?.Cancel();
            try { await diagnosticTask; } catch (OperationCanceledException) { }
            Engine.Dispose(); Engine = null;
            return;
        }
        await DisconnectAsync();
    }
}
