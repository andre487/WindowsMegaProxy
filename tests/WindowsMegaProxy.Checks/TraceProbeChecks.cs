using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MegaProxy.TraceProbe;

internal static class TraceProbeChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var payload = new byte[50_000_000];
        int rounds = 0, uploads = 0;
        long lastUpload = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "h3.speed.cloudflare.com" && request.RequestUri.AbsolutePath == "/cdn-cgi/trace")
            {
                if (lastUpload != 0) Assert(Stopwatch.GetElapsedTime(lastUpload).TotalMilliseconds >= 900, "Wait one second between completed speed rounds.");
                if (++rounds == 5) { stop.Cancel(); token.ThrowIfCancellationRequested(); }
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert(request.Content!.Headers.ContentLength == 50_000_000, "TraceProbe uploads 50 MB per round.");
                await request.Content.CopyToAsync(Stream.Null, token);
                uploads++; lastUpload = Stopwatch.GetTimestamp();
                return Response("ok");
            }
            if (request.RequestUri.AbsolutePath == "/__down")
            {
                if (request.RequestUri.Query == "?bytes=0") return Response("");
                Assert(request.RequestUri.Query == "?bytes=50000000", "TraceProbe downloads 50 MB per round.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload), Version = HttpVersion.Version20 };
            }
            return Response("ip=203.0.113.7\nloc=DE\ntls=TLSv1.3\n");
        }));
        using var output = new StringWriter();
        int exitCode = await ProbeRunner.RunAsync(client, output, stop.Token);
        string text = output.ToString();
        Assert(exitCode == 0 && uploads == 4 && text.Contains("ip=203.0.113.7"), "Print trace and continue through repeated complete speed rounds.");
        string lastTable = text[text.LastIndexOf("Run", StringComparison.Ordinal)..];
        Assert(Regex.Matches(lastTable, @"(?m)^\s*(\d+)\s").Select(match => match.Groups[1].Value).SequenceEqual(new[] { "2", "3", "4" }), "Keep only the latest three completed measurements in the current table.");
        Assert(Regex.Matches(lastTable, "Mbps").Count == 6 && Regex.Matches(lastTable, " ms").Count == 3, "Each recent result includes download, upload and HTTP latency.");

        using var cancel = new CancellationTokenSource();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stalled = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Query == "?bytes=50000000")
            {
                downloading.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return request.RequestUri.Query == "?bytes=0" ? Response("") : Response("ip=203.0.113.7\nloc=DE\n");
        }));
        using var cancelledOutput = new StringWriter();
        var running = ProbeRunner.RunAsync(stalled, cancelledOutput, cancel.Token);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        Assert(await running.WaitAsync(TimeSpan.FromSeconds(2)) == 0 && cancelledOutput.ToString().Contains("Stopped."), "Stop an in-flight speed request immediately on cancellation.");
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body), Version = HttpVersion.Version20 };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
