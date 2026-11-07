using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using WindowsMegaProxy;

internal static class FailoverChecks
{
    public static async Task RunIsolatedAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(FailoverChecks).Assembly.Location);
        start.ArgumentList.Add("--failover-check");
        var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(65), default);
        if (result.ExitCode != 0) throw new Exception("Fake-core failover failed: " + result.Error + result.Output);
        Console.WriteLine("Startup and traffic-triggered failover passed with fake cores and loopback only; no TUN driver.");
    }

    public static bool RunFixture(string[] args)
    {
        string? state = Environment.GetEnvironmentVariable("WINDOWSMEGAPROXY_FAILOVER_FIXTURE");
        if (state is null || !args.Contains("-c")) return false;
        var config = JsonNode.Parse(File.ReadAllText(args[Array.IndexOf(args, "-c") + 1]))!;
        if (args.Contains("tools"))
        {
            string host = config["outbounds"]!.AsArray().Single(item => item!["tag"]!.ToString() == "proxy")!["server"]!.ToString();
            if (host == "unavailable.invalid" || File.ReadAllText(state) == host)
            { Console.Error.WriteLine("outbound/http[proxy]: connection refused"); Environment.ExitCode = 1; }
            return true;
        }
        if (args[0] == "check") return true;
        if (args[0] != "run") throw new Exception("Unexpected fake-core command.");
        var endpoint = IPEndPoint.Parse(config["experimental"]!["clash_api"]!["external_controller"]!.ToString());
        using var listener = new TcpListener(endpoint); listener.Start();
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        ServeAsync(listener, stopping.Token).GetAwaiter().GetResult();
        return true;
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public static async Task RunAsync()
    {
        string directory = Path.GetFullPath(Path.Combine("artifacts/failover-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string state = Path.Combine(directory, "failed-host"); File.WriteAllText(state, "");
        const string variable = "WINDOWSMEGAPROXY_CORE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, Environment.ProcessPath);
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_FAILOVER_FIXTURE", state);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var serving = ServeAsync(origin, stopping.Token);
        using var engine = new Engine(directory, new Uri("http://127.0.0.1:" + ((IPEndPoint)origin.LocalEndpoint).Port + "/"));
        try
        {
            var profiles = new[] { "unavailable.invalid", "backup.invalid", "last.invalid" }.Select(host =>
            { var profile = ProxyProfile.Create(host); profile.Proxy["host"] = host; return profile; }).ToList();
            var settings = new Settings { Profiles = profiles, ActiveProfileId = profiles[0].Id, DefaultAction = "proxy" };
            settings.SharedOptions["failover"] = new JsonObject { ["mode"] = "ALL", ["profileIds"] = new JsonArray() };
            await engine.StartAsync(settings, cancellationToken: stopping.Token);
            if (engine.AppliedSettings?.ActiveProfileId != profiles[1].Id || engine.AppliedSignature != Configuration.Signature(settings))
                throw new Exception("Startup failover must use the next reachable profile without changing the requested selection/signature.");
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.RuntimeChanged += () => changed.TrySetResult();
            File.WriteAllText(state, "backup.invalid");
            var observe = typeof(Engine).GetMethod("ObserveProxyFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int index = 0; index < 3; index++) observe.Invoke(engine, ["outbound/http[proxy]: connection refused"]);
            await changed.Task.WaitAsync(stopping.Token);
            if (engine.AppliedSettings?.ActiveProfileId != profiles[2].Id || engine.AppliedSignature != Configuration.Signature(settings))
                throw new Exception("Traffic-triggered failover must skip attempted profiles, update the actual profile and retain the requested signature.");
            Console.WriteLine("Startup and traffic-triggered failover passed with fake cores and loopback only; no TUN driver.");
        }
        finally
        {
            await engine.StopAsync();
            stopping.Cancel(); await serving;
            Environment.SetEnvironmentVariable(variable, previous);
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_FAILOVER_FIXTURE", null);
        }
    }
}
