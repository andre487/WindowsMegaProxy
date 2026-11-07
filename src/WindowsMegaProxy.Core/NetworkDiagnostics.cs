using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record DiagnosticResult(string ExitIp, string? Country, int LatencyMs, string Http3);
public sealed record SpeedResult(string Route, string Ip = "—", string Country = "—", string Protocol = "—",
    string Latency = "—", string Download = "—", string Upload = "—", string Status = "Waiting");

public static class NetworkDiagnostics
{
    public static string? ParseIp(string text) => text.Trim().Length <= 128 && IPAddress.TryParse(text.Trim(), out var ip) ? ip.ToString() : null;
    public static string? ParseCountry(string text)
    {
        text = text.Trim().ToUpperInvariant();
        return text.Length == 2 && text.All(value => value is >= 'A' and <= 'Z') ? text : null;
    }
    public static string TraceValue(string text, string key) => text.Split('\n').FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim() ?? "";

    public static async Task<string> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), token)) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new InvalidDataException("Service response exceeds the size limit.");
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<string> FetchAsync(HttpClient client, string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response.Content, 16384, timeout.Token);
    }

    public static async Task<DiagnosticResult> CheckAsync(HttpClient client, bool throughProfile, Action<string> log, CancellationToken token)
    {
        var callerToken = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        token = deadline.Token;
        log("HTTPS: checking example.com and its server certificate.");
        var watch = Stopwatch.StartNew();
        await FetchAsync(client, "https://example.com/", token);
        int latency = (int)Math.Max(1, watch.ElapsedMilliseconds);
        log("HTTPS check passed.");
        string? ip = null, country = null;
        try
        {
            var trace = await FetchAsync(client, "https://speed.cloudflare.com/cdn-cgi/trace", token);
            ip = ParseIp(TraceValue(trace, "ip")); country = ParseCountry(TraceValue(trace, "loc"));
        }
        catch (Exception error) when (Recoverable(error, token)) { log("Cloudflare trace unavailable; trying fallback services."); }
        if (ip is null)
            foreach (var url in new[] { "https://ifconfig.me/ip", "https://api.ipify.org/", "https://icanhazip.com/" })
            {
                try { ip = ParseIp(await FetchAsync(client, url, token)); if (ip is not null) break; }
                catch (Exception error) when (Recoverable(error, token)) { log("Fallback IP service unavailable."); }
            }
        if (ip is null) throw new InvalidDataException("Could not determine the tested route's public IP.");
        if (country is null)
            foreach (var url in new[] { "https://ifconfig.co/country-iso", "https://ipapi.co/country_code/", "https://api.country.is/" })
            {
                try
                {
                    string body = await FetchAsync(client, url, token);
                    country = ParseCountry(url.EndsWith("country.is/", StringComparison.Ordinal) ? JsonNode.Parse(body)?["country"]?.ToString() ?? "" : body);
                    if (country is not null) break;
                }
                catch (Exception error) when (Recoverable(error, token)) { log("Fallback country service unavailable."); }
            }
        log("Public IP determined; " + (country is null ? "country unavailable." : "country determined."));
        token = callerToken;
        if (throughProfile) return new(ip, country, latency, "");
        var h3 = new List<string>();
        foreach (var url in new[] { "https://www.cloudflare.com/cdn-cgi/trace", "https://quic.browserleaks.com/" })
        {
            string status = "unavailable";
            if (!throughProfile)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await ReadBoundedAsync(response.Content, 65536, timeout.Token);
                    if (response.Version.Major == 3) status = "confirmed";
                }
                catch (Exception error) when (Recoverable(error, token) || error is NotSupportedException) { }
            }
            h3.Add($"HTTP/3 · {new Uri(url).Host}: {status}");
        }
        string http3 = string.Join(Environment.NewLine, h3);
        log(http3);
        return new(ip, country, latency, http3);
    }

    private static bool Recoverable(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        error is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException;

    public static async Task<SpeedResult> MeasureAsync(HttpClient client, string route, int megabytes, string preferred,
        bool throughProfile, Action<SpeedResult> update, Action<string> log, CancellationToken token)
    {
        if (megabytes is not (10 or 25 or 50) || preferred is not ("h1" or "h2" or "h3")) throw new ArgumentException("Invalid speed test parameters.");
        const string origin = "https://h3.speed.cloudflare.com";
        var result = new SpeedResult(route, Status: "Running");
        update(result);
        Version negotiated = HttpVersion.Version11;
        foreach (var protocol in preferred == "h3" ? new[] { "h3", "h2" } : new[] { preferred })
        {
            if (protocol == "h3" && throughProfile) { log(route + ": HTTP/3 testing through a profile is unavailable; using HTTP/2 or HTTP/1.1."); continue; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var request = Request(HttpMethod.Get, origin + "/cdn-cgi/trace", protocol == "h3" ? HttpVersion.Version30 : protocol == "h2" ? HttpVersion.Version20 : HttpVersion.Version11);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                string trace = await ReadBoundedAsync(response.Content, 16384, timeout.Token);
                string ip = ParseIp(TraceValue(trace, "ip")) ?? throw new InvalidDataException("Invalid IP in speed service metadata.");
                negotiated = response.Version;
                result = result with { Ip = ip, Country = ParseCountry(TraceValue(trace, "loc")) ?? "—", Protocol = $"HTTP/{negotiated} · {TraceValue(trace, "tls")}" };
                update(result);
                break;
            }
            catch (Exception error) when (protocol == "h3" && (Recoverable(error, token) || error is NotSupportedException) && error is not HttpRequestException { StatusCode: not null } && !HasCertificateError(error))
            { log(route + ": HTTP/3 unavailable; using HTTP/2 or HTTP/1.1."); }
        }
        var latency = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            using var request = Request(HttpMethod.Get, origin + "/__down?bytes=0", negotiated, exact: true);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            latency.Add(watch.ElapsedMilliseconds);
            await ReadBoundedAsync(response.Content, 1024, token);
        }
        result = result with { Latency = $"{latency.Order().ElementAt(1)} ms" }; update(result);
        long bytes = megabytes * 1_000_000L;
        foreach (bool upload in new[] { false, true })
        {
            log(route + (upload ? ": uploading " : ": downloading ") + megabytes + " MB.");
            using var request = Request(upload ? HttpMethod.Post : HttpMethod.Get, origin + (upload ? "/__up" : "/__down?bytes=" + bytes), negotiated, exact: true);
            using var payload = upload ? new ZeroContent(bytes) : null;
            if (payload is not null) request.Content = payload;
            var watch = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65536];
            long received = 0, limit = upload ? 65536 : bytes;
            int count;
            while ((count = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1 - received)), token)) > 0)
            {
                received += count;
                if (received > limit) throw new InvalidDataException("The speed service returned excess data.");
            }
            if ((!upload && received != bytes) || (upload && payload!.Sent != bytes)) throw new EndOfStreamException("The transfer did not complete.");
            string speed = $"{bytes * 8 / Math.Max(0.001, watch.Elapsed.TotalSeconds) / 1_000_000:0.##} Mbps";
            result = upload ? result with { Upload = speed, Status = "Done" } : result with { Download = speed };
            update(result);
        }
        return result;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, Version version, bool exact = false)
    {
        var request = new HttpRequestMessage(method, url) { Version = version, VersionPolicy = exact || version.Major == 3 ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower };
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return request;
    }

    private static bool HasCertificateError(Exception error) => error is AuthenticationException || (error.InnerException is not null && HasCertificateError(error.InnerException));

    private sealed class ZeroContent(long length) : HttpContent
    {
        public long Sent { get; private set; }
        protected override bool TryComputeLength(out long size) { size = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            var buffer = new byte[65536];
            Sent = 0;
            while (Sent < length)
            {
                int count = (int)Math.Min(buffer.Length, length - Sent);
                await stream.WriteAsync(buffer.AsMemory(0, count), token);
                Sent += count;
            }
        }
    }
}
