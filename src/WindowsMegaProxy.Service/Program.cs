using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal sealed class Program : ServiceBase
{
    private readonly CancellationTokenSource stopping = new();
    private Task run = Task.CompletedTask;
    private IDisposable? runtime;

    private Program() { ServiceName = ServiceProtocol.Name; CanShutdown = true; }
    private static int Main(string[] args)
    {
        Ui.SetLanguage("en");
        if (args.Length != 0) return Installation.Run(args);
        Run(new Program());
        return 0;
    }

    protected override void OnStart(string[] args)
    {
        RequestAdditionalTime(20000);
        Installation.VerifyBootUpdate();
        runtime = StartupConfiguration.ClaimRuntime();
        run = Task.Run(RunAsync);
    }
    protected override void OnStop()
    {
        RequestAdditionalTime(20000);
        StopCore();
    }
    private void StopCore()
    {
        stopping.Cancel();
        try { run.WaitAsync(TimeSpan.FromSeconds(18)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Fatal(error); }
        finally { runtime?.Dispose(); runtime = null; }
    }
    protected override void OnShutdown() => StopCore();

    private async Task RunAsync()
    {
        try
        {
            var store = new MachineSettingsStore();
            await using var broker = new ServiceBroker(store, SetAutostartAsync);
            var server = new ServicePipeServer(broker);
            var maintenance = broker.MaintainAsync(stopping.Token);
            var serving = server.RunAsync(stopping.Token);
            try
            {
                await ServiceLifetime.WatchAsync(serving, maintenance, stopping.Token);
            }
            catch (Exception error) when (!stopping.IsCancellationRequested) { Fatal(error); }
            finally
            {
                stopping.Cancel();
                try { await Task.WhenAll(serving, maintenance).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            Fatal(error);
        }
    }

    private void Fatal(Exception error)
    {
        ServiceLifetime.ExitAfterFailure(() => EventLog.WriteEntry(ServiceName, DiagnosticLog.Redact(error.ToString()), EventLogEntryType.Error));
    }

    private static Task SetAutostartAsync(bool enabled)
    {
        // Startup mode is an explicit elevated operation; imported legacy flags cannot install autorun.
        return Task.CompletedTask;
    }
}
