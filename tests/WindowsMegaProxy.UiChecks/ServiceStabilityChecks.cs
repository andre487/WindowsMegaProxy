using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class ServiceStabilityChecks
{
    private static readonly ClientAccess Admin = new("test-admin", true);

    public static async Task RunAsync()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (bool failMaintenance in new[] { false, true })
        {
            var failure = Task.FromException(new InvalidOperationException("Injected worker failure."));
            bool observed = false;
            try { await ServiceLifetime.WatchAsync(failMaintenance ? pending.Task : failure, failMaintenance ? failure : pending.Task, CancellationToken.None); }
            catch (InvalidOperationException) { observed = true; }
            Assert(observed, "Failure of either required worker is observed immediately.");
        }
        bool unexpectedCompletion = false;
        try { await ServiceLifetime.WatchAsync(Task.CompletedTask, pending.Task, CancellationToken.None); }
        catch (IOException) { unexpectedCompletion = true; }
        Assert(unexpectedCompletion, "Unexpected successful worker completion is also fatal.");
        using (var shutdown = new CancellationTokenSource())
        {
            shutdown.Cancel();
            await ServiceLifetime.WatchAsync(Task.CompletedTask, pending.Task, shutdown.Token);
        }
        var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(childStart.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            childStart.ArgumentList.Add(typeof(ServiceStabilityChecks).Assembly.Location);
        childStart.ArgumentList.Add("--service-fatal-fixture");
        using (var child = Process.Start(childStart)!)
        {
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert(child.ExitCode == 1, "Fatal service failure exits even when diagnostic reporting throws.");
            }
            finally { if (!child.HasExited) child.Kill(true); }
        }
        string root = Path.GetFullPath("artifacts/service-stability/" + Guid.NewGuid().ToString("N"));
        var store = new MachineSettingsStore(root, machine: false);
        store.Save(new Settings { Language = "en", AutoConnect = true });
        await using (var retries = new ServiceBroker(store, _ => Task.CompletedTask,
            () => new TestEngine { Start = _ => throw new TimeoutException("Network unavailable.") }))
        {
            foreach (int seconds in new[] { 5, 10, 20, 40, 60, 60 })
            {
                Due(retries); await Recover(retries);
                var next = (DateTimeOffset)typeof(ServiceBroker).GetField("nextRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retries)!;
                Assert(Math.Abs((next - DateTimeOffset.UtcNow).TotalSeconds - seconds) < 2, "Recovery uses bounded exponential backoff.");
            }
        }
        var core = new TestEngine();
        await using (var broker = new ServiceBroker(store, _ => Task.CompletedTask, () => core))
        {
            core.Start = _ => throw new TimeoutException("Network is not ready.");
            await Recover(broker);
            Assert(core.Starts == 1 && !core.Running, "Failed boot connection remains retryable.");
            await Recover(broker);
            Assert(core.Starts == 1, "Backoff prevents immediate retry.");
            core.Start = null;
            Due(broker);
            await Recover(broker);
            Assert(core.Running && core.Starts == 2, "Connection recovers when the network returns.");

            var hello = await Call(broker, new("hello"));
            var edited = hello.Settings!.Copy(); edited.Theme = "dark";
            await Call(broker, new("save", Settings: edited, Revision: hello.Revision));
            core.Exit(); Due(broker); await Recover(broker);
            Assert(core.Running && core.Starts == 3 && core.AppliedSettings!.Theme != "dark", "Crash recovery retains applied settings, not unapplied edits.");
            bool staleStopRejected = false;
            try { await Call(broker, new("stop", Generation: hello.Status!.Generation)); }
            catch (ServiceStateChangedException) { staleStopRejected = true; }
            Assert(staleStopRejected && core.Running, "Stale client cleanup cannot stop a recovered connection.");

            core.Healthy = false;
            for (int i = 0; i < 2; i++) { Due(broker); await Recover(broker); }
            Assert(core.Starts == 3, "Two API failures do not restart the core.");
            core.Healthy = true; Due(broker); await Recover(broker);
            core.Healthy = false;
            for (int i = 0; i < 3; i++) { Due(broker); await Recover(broker); }
            Assert(core.Starts == 4, "Three consecutive API failures restart the core.");
            core.Healthy = true;

            var status = await Call(broker, new("status"));
            await Call(broker, new("stop", Generation: status.Status!.Generation));
            Due(broker); await Recover(broker);
            Assert(!core.Running && core.Starts == 4, "Manual stop cancels automatic recovery even with AutoConnect enabled.");

            core.Start = _ => throw new IOException("Certificate verification failed.");
            try { await Call(broker, new("start")); } catch (IOException) { }
            Due(broker); await Recover(broker);
            Assert(core.Starts == 5, "Unclassified validation failures are not retried.");
            core.Start = null;

            var diagnostic = await Call(broker, new("start", TestOnly: true));
            core.Exit(); Due(broker); await Recover(broker);
            Assert(core.Starts == 6, "Diagnostic core exit does not create a persistent connection.");
            core.Stop = () => throw new IOException("Injected cleanup failure.");
            Set(broker, "leaseDeadline", DateTimeOffset.MinValue);
            using var stop = new CancellationTokenSource();
            Task maintenance = broker.MaintainAsync(stop.Token);
            await Task.Delay(2300);
            Assert(!maintenance.IsCompleted && !(await Call(broker, new("hello"))).Testing, "Lease cleanup failure does not kill maintenance or IPC.");
            stop.Cancel();
            try { await maintenance; } catch (OperationCanceledException) { }
            core.Stop = null;
        }

        string path = Path.Combine(root, "settings.json");
        foreach (string invalid in new[] { "{", "null", "[]", "{\"version\":1}", "{\"version\":1,\"scope\":\"user\",\"protectedSettings\":\"AQ==\"}" })
        {
            File.WriteAllText(path, invalid);
            await using var broker = new ServiceBroker(store, _ => Task.CompletedTask, () => new TestEngine());
            var reply = await Call(broker, new("hello"));
            Assert(reply.CanEdit && reply.Text is not null, "Corrupt settings retain repair IPC and expose a warning.");
            bool rejected = false;
            try { await Call(broker, new("start")); } catch (IOException) { rejected = true; }
            Assert(rejected && File.ReadAllText(path) == invalid, "Recovery does not silently replace settings or start TUN.");
            await Call(broker, new("save", Settings: new Settings(), Revision: reply.Revision));
            Assert(Directory.GetFiles(root, "settings-unreadable-*.json").Any(file => File.ReadAllText(file) == invalid), "Explicit repair preserves the original protected file.");
            Assert((await Call(broker, new("hello"))).Text is null, "Successful repair clears recovery mode.");
            store.Load();
        }

        using var lists = new CancellationTokenSource();
        using var native = new Engine(Path.Combine(root, "native"));
        Set(native, "failoverTask", Task.FromException(new IOException("Injected failover failure.")));
        Set(native, "listUpdates", Task.FromException(new UnauthorizedAccessException("Injected list failure.")));
        Set(native, "listStopping", lists);
        string runtime = Path.Combine(root, "runtime-test.json"); File.WriteAllText(runtime, "{}");
        Set(native, "configPath", runtime);
        await native.StopAsync();
        Assert(!File.Exists(runtime), "Faulted workers cannot bypass runtime cleanup.");
        Console.WriteLine("Service startup retry, crash recovery, health threshold, manual stop, diagnostic isolation, corrupt-settings repair and failed-worker cleanup passed; no TUN.");
    }

    private static Task<ServiceReply> Call(ServiceBroker broker, ServiceRequest request) =>
        broker.HandleAsync(Admin, request with { InstanceId = broker.InstanceId }, CancellationToken.None);
    private static Task Recover(ServiceBroker broker) =>
        (Task)typeof(ServiceBroker).GetMethod("RecoverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(broker, [CancellationToken.None])!;
    private static void Due(ServiceBroker broker)
    {
        Set(broker, "nextRecovery", DateTimeOffset.MinValue);
        Set(broker, "nextHealthCheck", DateTimeOffset.MinValue);
    }
    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
