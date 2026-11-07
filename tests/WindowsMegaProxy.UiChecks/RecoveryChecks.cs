using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal sealed class RecoveryFixture : IAsyncDisposable
{
    public ServiceBroker Broker { get; }
    public TestEngine Engine { get; } = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task serving, maintenance;
    public RecoveryFixture(MachineSettingsStore store, string pipe)
    {
        Broker = new(store, _ => Task.CompletedTask, () => Engine);
        var admin = new ClientAccess(WindowsIdentity.GetCurrent().User!.Value, true);
        serving = new ServicePipeServer(Broker, pipe, machine: false, _ => admin).RunAsync(stop.Token);
        maintenance = Broker.MaintainAsync(stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await serving;
        try { await maintenance; } catch (OperationCanceledException) { }
        await Broker.DisposeAsync();
        stop.Dispose();
    }
}

internal static class RecoveryChecks
{
    public static bool RunFixture(string[] args)
    {
        if (args.Length != 4 || args[0] != "--diagnostic-owner") return false;
        var client = new ServiceClient(args[1], args[2]);
        client.CallAsync(new("hello")).GetAwaiter().GetResult();
        var started = client.CallAsync(new("start", TestOnly: true)).GetAwaiter().GetResult();
        File.WriteAllText(args[3], started.Lease);
        Thread.Sleep(Timeout.Infinite);
        return true;
    }

    public static async Task RunAsync()
    {
        string root = Path.GetFullPath("artifacts/recovery-checks/" + Guid.NewGuid().ToString("N"));
        var store = new MachineSettingsStore(root, machine: false);
        string pipe = "WindowsMegaProxy.Recovery." + Guid.NewGuid().ToString("N");
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        var client = new ServiceClient(pipe, sid);
        var config = new Settings { Language = "en", Theme = "light" };
        store.Save(config);
        int migrations = 0;
        Task<Settings> Legacy() { migrations++; return Task.FromResult(config.Copy()); }
        string firstInstance;
        long revision;
        long cachedGeneration;
        RemoteEngine oldTest;
        await using (var first = new RecoveryFixture(store, pipe))
        {
            var initial = await client.InitializeAsync(Legacy);
            firstInstance = initial.InstanceId!;
            revision = initial.Revision;
            cachedGeneration = initial.Status!.Generation;
            Assert(migrations == 0 && initial.Initialized, "Existing shared configuration is loaded without importing personal settings.");
            await client.InitializeAsync(Legacy);
            Assert(migrations == 0, "Rebinding to initialized storage must not import personal settings again.");
            oldTest = new RemoteEngine(client, _ => Task.FromResult(false));
            await oldTest.StartAsync(config, testOnly: true);
            var latest = config.Copy(); latest.Theme = "dark";
            await client.CallAsync(new("save", Settings: latest, Revision: revision));
        }
        await using (var replacement = new RecoveryFixture(store, pipe))
        {
            var status = await client.CallAsync(new("status"));
            Assert(status.Revision == revision && status.Status!.Generation == cachedGeneration, "The test reproduces counter overlap after a restart.");
            Assert(status.InstanceId != firstInstance && client.InstanceId == firstInstance,
                "Polling detects a replacement without silently rebinding pending mutations.");
            await RejectAsync(() => client.CallAsync(new("save", Settings: config, Revision: 0)), "A stale save cannot cross a service restart.");
            var restored = await client.InitializeAsync(Legacy);
            Assert(restored.Settings!.Theme == "dark" && migrations == 0, "A new handshake reads the replacement's durable configuration.");
            var live = new RemoteEngine(client, _ => Task.FromResult(false));
            await live.StartAsync(config);
            await oldTest.StopAsync();
            Assert(replacement.Engine.Running, "Old diagnostic cleanup cannot stop the replacement's global connection.");
            await RejectAsync(() => client.CallAsync(new("stop", Lease: "old-token", Generation: live.Generation)),
                "A test token is rejected when the current connection has no test lease.");
            var stale = new RemoteEngine(client, _ => Task.FromResult(false));
            stale.Apply(await client.CallAsync(new("hello")));
            await live.StopAsync();
            await live.StartAsync(config);
            await stale.StopAsync();
            Assert(replacement.Engine.Running, "Cleanup of an older generation cannot stop the newer generation in the same service.");
            await live.StopAsync();

            string marker = Path.Combine(root, "owner-ready.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(RecoveryChecks).Assembly.Location);
            foreach (string argument in new[] { "--diagnostic-owner", pipe, sid, marker }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
                Assert(replacement.Engine.Running && (await client.CallAsync(new("status"))).Testing, "A live diagnostic owner is visible after another client attaches.");
                child.Kill(); await child.WaitForExitAsync();
                while (replacement.Engine.Running) await Task.Delay(20, timeout.Token);
                Assert(!(await client.CallAsync(new("status"))).Testing, "A dead client releases its diagnostic runtime without the six-minute delay.");
                await live.StartAsync(config);
                Assert(replacement.Engine.Running, "A reopened client can immediately connect after diagnostic-owner cleanup.");
                await live.StopAsync();
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        Console.WriteLine("Service restart, stale mutations/cleanup, migration and diagnostic-owner process death passed; no TUN.");
    }

    private static async Task RejectAsync(Func<Task<ServiceReply>> action, string message)
    {
        bool rejected = false;
        try { await action(); } catch (ServiceStateChangedException) { rejected = true; }
        Assert(rejected, message);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
