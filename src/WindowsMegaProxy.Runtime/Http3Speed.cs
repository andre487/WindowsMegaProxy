using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class Http3Speed
{
    public static async Task<SpeedResult?> MeasureAsync(ProbeAccess? access, string route, int megabytes,
        Action<SpeedResult> update, Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (megabytes is not (10 or 25 or 50)) throw new ArgumentException("Invalid speed test size.");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WindowsMegaProxy.Diagnostics.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        using var job = new ProcessJob();
        using var process = Process.Start(start) ?? throw new IOException("Could not start the HTTP/3 diagnostic client.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var cancellation = timeout.Token;
        Task errors = Task.CompletedTask;
        try
        {
            job.Assign(process);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Port = access?.Port ?? 0, Secret = access?.Secret ?? "", Megabytes = megabytes }).AsMemory(), cancellation);
            process.StandardInput.Close();
            // Never place credentials on the command line or echo helper stderr into the user log.
            errors = DrainAsync(process.StandardError, cancellation);
            SpeedResult? result = null;
            bool fallback = false;
            int lines = 0;
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                if (++lines > 16 || line.Length > 8192) throw new IOException("Invalid diagnostic output.");
                var item = JsonNode.Parse(line)!.AsObject();
                if (item["Error"] is not null) throw new IOException("HTTP/3 speed test failed.");
                if (item["Fallback"]?.GetValue<bool>() == true) { fallback = true; continue; }
                if (item["Result"] is { } value)
                {
                    result = value.Deserialize<SpeedResult>()! with { Route = route };
                    update(result);
                }
            }
            await Task.WhenAll(errors, process.WaitForExitAsync(cancellation));
            if (process.ExitCode != 0) throw new IOException("HTTP/3 diagnostic client stopped unexpectedly.");
            if (fallback && result is null) { log(route + ": HTTP/3 negotiation unavailable; trying HTTP/2 or HTTP/1.1 on the same route."); return null; }
            if (result?.Status != "Done") throw new IOException("HTTP/3 speed test did not finish.");
            return result;
        }
        finally
        {
            timeout.Cancel();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            try { await errors; }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token) > 0) { }
    }
}
