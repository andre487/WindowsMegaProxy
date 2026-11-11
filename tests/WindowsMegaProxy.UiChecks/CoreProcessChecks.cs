using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class CoreProcessChecks
{
    private static ProcessStartInfo Self(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CoreProcessChecks).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static bool RunFixture(string[] args)
    {
        if (args.Length == 0) return false;
        if (args[0] == "--process-output") { Console.Write(new string('x', 70000)); Thread.Sleep(Timeout.Infinite); return true; }
        string? marker = args[0] is "stable" or "experimental" ? Environment.GetEnvironmentVariable("WINDOWSMEGAPROXY_CHECK_FIXTURE")
            : args.Length == 2 && args[0] is "--process-hang" or "--process-leaf" ? args[1] : null;
        if (marker is null) return false;
        if (args[0] is "stable" or "experimental") _ = NetworkCorePaths.ReadRequest();
        File.WriteAllText(marker, Environment.ProcessId.ToString());
        if (args[0] != "--process-leaf")
        {
            using var leaf = Process.Start(Self("--process-leaf", marker + ".leaf"));
        }
        Thread.Sleep(Timeout.Infinite);
        return true;
    }

    private static async Task WaitForMarkers(string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker) || !File.Exists(marker + ".leaf")) await Task.Delay(20, timeout.Token);
    }

    private static async Task AssertStopped(string marker)
    {
        foreach (string path in new[] { marker, marker + ".leaf" })
        {
            int pid = int.Parse(await File.ReadAllTextAsync(path));
            try
            {
                using var process = Process.GetProcessById(pid);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { }
            File.Delete(path);
        }
    }

    public static async Task RunAsync()
    {
        string directory = Path.GetFullPath(Path.Combine("artifacts/process-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        using (var cancellation = new CancellationTokenSource())
        {
            var command = CoreProcess.RunAsync(Self("--process-hang", marker), TimeSpan.FromSeconds(30), cancellation.Token);
            await WaitForMarkers(marker);
            cancellation.Cancel();
            try { await command; throw new Exception("A cancelled process kept running."); }
            catch (OperationCanceledException) { }
            await AssertStopped(marker);
        }
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try { await CoreProcess.RunAsync(Self("--process-hang", marker), TimeSpan.FromSeconds(2), timeout.Token); throw new Exception("The process deadline was ignored."); }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            await AssertStopped(marker);
        }
        try { await CoreProcess.RunAsync(Self("--process-output"), TimeSpan.FromSeconds(10), CancellationToken.None); throw new Exception("Unbounded process output was accepted."); }
        catch (IOException) { }

        string? previousCore = Environment.GetEnvironmentVariable("WINDOWSMEGAPROXY_CORE");
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", NetworkCorePaths.FixtureHost());
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CHECK_FIXTURE", marker);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var engine = new Engine(directory, allowEnvironmentOverrides: true);
            var startup = engine.StartAsync(new Settings(), testOnly: true, cancellation.Token);
            await WaitForMarkers(marker);
            cancellation.Cancel();
            try { await startup; throw new Exception("Core startup ignored cancellation."); }
            catch (OperationCanceledException) { }
            await AssertStopped(marker);
            if (Directory.GetFiles(directory, "*.json").Length > 0) throw new Exception("Cancelled validation left a runtime configuration behind.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", previousCore);
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CHECK_FIXTURE", null);
        }
    }
}
