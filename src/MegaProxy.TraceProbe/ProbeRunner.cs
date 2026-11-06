using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

namespace MegaProxy.TraceProbe;

public static class ProbeRunner
{
    public static async Task<int> RunAsync(HttpClient client, TextWriter output, CancellationToken token)
    {
        int exitCode = 0;
        int round = 0;
        var recent = new Queue<(int Number, DateTime Time, SpeedResult Result)>();
        try
        {
            await output.WriteLineAsync("GET https://www.cloudflare.com/cdn-cgi/trace\n");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await output.WriteLineAsync(await client.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", deadline.Token));
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
            {
                await output.WriteLineAsync("Trace request failed: " + error.Message);
                exitCode = 1;
            }
            await output.WriteLineAsync("Continuous Cloudflare test: 50 MB download + 50 MB upload per round, with a 1-second pause. Press Enter or Ctrl+C to stop.");
            await output.WriteLineAsync("Latency is HTTP response time, not ICMP ping. Showing the last three completed measurements.\n");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromMinutes(2));
                    var result = await NetworkDiagnostics.MeasureAsync(client, "TUN route", 50, "h2", false, _ => { }, _ => { }, deadline.Token);
                    recent.Enqueue((++round, DateTime.Now, result));
                    if (recent.Count > 3) recent.Dequeue();
                    await output.WriteLineAsync($"{"Run",-5} {"Time",-10} {"Latency",-14} {"Download",-20} {"Upload",-20}");
                    foreach (var sample in recent)
                        await output.WriteLineAsync($"{sample.Number,-5} {sample.Time:HH:mm:ss}   {sample.Result.Latency,-14} {sample.Result.Download,-20} {sample.Result.Upload,-20}");
                    await output.WriteLineAsync();
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
                {
                    await output.WriteLineAsync("Speed test failed: " + error.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await output.WriteLineAsync("Stopped."); }
        return exitCode;
    }
}
