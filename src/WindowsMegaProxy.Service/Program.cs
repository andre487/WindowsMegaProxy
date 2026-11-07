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

    private Program() { ServiceName = ServiceProtocol.Name; CanShutdown = true; }
    private static void Main() { Ui.SetLanguage("en"); Run(new Program()); }

    protected override void OnStart(string[] args) => run = Task.Run(RunAsync);
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
                    try { await broker.HandleAsync(new("S-1-5-18", true), new("start"), stopping.Token); }
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
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled) key.SetValue(ServiceProtocol.Name, $"\"{Path.Combine(AppContext.BaseDirectory, "WindowsMegaProxy.exe")}\" --tray", RegistryValueKind.String);
        else key.DeleteValue(ServiceProtocol.Name, false);
        return Task.CompletedTask;
    }
}
