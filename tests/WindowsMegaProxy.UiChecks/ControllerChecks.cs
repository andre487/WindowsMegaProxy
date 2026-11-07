using System;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class ControllerChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var settings = new Settings();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new TestEngine { Start = token => { started.SetResult(); return Task.Delay(Timeout.Infinite, token); } };
        int created = 0;
        var controller = new ConnectionController(() => { created++; return slow; });
        var connecting = controller.ConnectAsync(settings);
        await started.Task;
        bool tested = await controller.RunDiagnosticsAsync(settings, (_, _) => throw new Exception("Diagnostics overlapped startup."));
        Assert(!tested && created == 1 && controller.State == ConnectionState.Connecting, "Opening diagnostics during startup cannot create a second engine.");
        await controller.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try { await connecting; throw new Exception("Startup ignored cancellation."); }
        catch (OperationCanceledException) { }
        Assert(slow.Disposed && slow.Stops == 1 && controller.Engine is null && controller.State == ConnectionState.Disconnected,
            "Shutdown cancels startup and owns cleanup before completing.");

        var temporary = new TestEngine();
        var testing = new ConnectionController(() => temporary);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = testing.RunDiagnosticsAsync(settings, (_, token) => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); });
        await entered.Task;
        bool rejected = false;
        try { await testing.ConnectAsync(settings); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && testing.IsTesting, "An offline diagnostic owns the engine until it finishes.");
        await testing.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try { await diagnostic; throw new Exception("Diagnostic ignored cancellation."); }
        catch (OperationCanceledException) { }
        Assert(temporary.Disposed && !testing.IsTesting, "Shutdown waits for temporary-core cleanup.");

        var live = new TestEngine();
        var connected = new ConnectionController(() => live);
        await connected.ConnectAsync(settings);
        int unexpected = 0;
        connected.UnexpectedExit += () => unexpected++;
        Assert(await connected.RunDiagnosticsAsync(settings, (engine, _) =>
        {
            Assert(ReferenceEquals(engine, live), "A connected diagnostic reuses the applied engine.");
            return Task.CompletedTask;
        }), "A connected diagnostic runs.");
        Assert(live.Stops == 0 && !live.Disposed, "Completing diagnostics cannot stop the live connection.");
        live.Exit();
        Assert(unexpected == 1, "An unexpected core exit is surfaced once.");
        await connected.DisconnectAsync();
        Assert(live.Disposed && connected.State == ConnectionState.Disconnected, "A crashed core is cleaned up through the same lifecycle.");
    }
}
