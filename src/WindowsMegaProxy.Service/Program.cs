using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
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
        try { run.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
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
            Task startup = Task.CompletedTask;
            if (broker.AutoConnect)
                startup = Task.Run(async () =>
                {
                    try { await broker.HandleAsync(new("S-1-5-18", true), new("start", InstanceId: broker.InstanceId), stopping.Token); }
                    catch (Exception error) { EventLog.WriteEntry(ServiceName, DiagnosticLog.Redact(error.Message), EventLogEntryType.Error); }
                });
            try { await server.RunAsync(stopping.Token); }
            finally
            {
                stopping.Cancel();
                try { await maintenance; } catch (OperationCanceledException) { }
                await startup;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            EventLog.WriteEntry(ServiceName, DiagnosticLog.Redact(error.ToString()), EventLogEntryType.Error);
            Environment.Exit(1);
        }
    }

    private static Task SetAutostartAsync(bool enabled)
    {
        // Startup mode is an explicit elevated operation; imported legacy flags cannot install autorun.
        return Task.CompletedTask;
    }
}
