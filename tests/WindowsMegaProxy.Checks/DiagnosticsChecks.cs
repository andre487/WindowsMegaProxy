using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class DiagnosticsChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync()
    {
        Assert(NetworkDiagnostics.ParseIp("not-an-ip") is null && NetworkDiagnostics.ParseCountry("Germany") is null, "Validate external identity responses.");
        var calls = new List<string>();
        using var handler = new Handler(async (request, token) =>
        {
            string url = request.RequestUri!.ToString(); calls.Add(url);
            if (request.Method == HttpMethod.Post)
            {
                Assert(request.Content!.Headers.ContentLength == 10_000_000, "Known upload length.");
                await request.Content.CopyToAsync(Stream.Null, token);
                return Response("ok");
            }
            if (url.Contains("/__down?bytes=10000000")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10_000_000]), Version = HttpVersion.Version20 };
            if (url.Contains("/__down?bytes=0")) return Response("");
            if (request.RequestUri.Host == "example.com") return Response("ok");
            return Response("ip=203.0.113.7\nloc=DE\ntls=TLSv1.3\n");
        });
        using var client = new HttpClient(handler);
        var result = await NetworkDiagnostics.CheckAsync(client, true, _ => { }, CancellationToken.None);
        Assert(result.ExitIp == "203.0.113.7" && result.Country == "DE", "IP and country use the same trace response.");
        Assert(!calls.Exists(url => url.Contains("quic.browserleaks")) && result.Http3 == "", "TCP diagnostics never bypass a profile or invent an HTTP/3 result.");
        SpeedResult? last = null;
        var speed = await NetworkDiagnostics.MeasureAsync(client, "test", 10, "h3", true, value => last = value, _ => { }, CancellationToken.None);
        Assert(speed.Download.Contains("Mbps") && speed.Upload.Contains("Mbps") && last == speed, "Bounded streaming download and upload publish both results.");
        Assert(speed.Protocol.StartsWith("HTTP/2"), "Actual negotiated protocol is shown after h3 fallback.");
        using var oversized = new ByteArrayContent(new byte[17000]);
        try { await NetworkDiagnostics.ReadBoundedAsync(oversized, 16384, CancellationToken.None); throw new Exception("Oversized metadata accepted."); }
        catch (InvalidDataException) { }
        using var fallback = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.Host switch
        {
            "example.com" => Response("ok"),
            "speed.cloudflare.com" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "ifconfig.me" => Response("203.0.113.9"),
            "ifconfig.co" => Response("nl"),
            _ => throw new Exception("Unexpected fallback provider.")
        })));
        var recovered = await NetworkDiagnostics.CheckAsync(fallback, true, _ => { }, CancellationToken.None);
        Assert(recovered.ExitIp == "203.0.113.9" && recovered.Country == "NL", "IP and country fallbacks match Android.");
        int certificateAttempts = 0;
        using var badCertificate = new HttpClient(new Handler((_, _) =>
        {
            certificateAttempts++;
            throw new HttpRequestException("Synthetic certificate error", new AuthenticationException());
        }));
        try { await NetworkDiagnostics.MeasureAsync(badCertificate, "test", 10, "h3", false, _ => { }, _ => { }, CancellationToken.None); throw new Exception("Certificate failure ignored."); }
        catch (HttpRequestException) { Assert(certificateAttempts == 1, "Certificate errors must not trigger protocol fallback."); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await NetworkDiagnostics.CheckAsync(client, true, _ => { }, cancelled.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body), Version = HttpVersion.Version20 };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
