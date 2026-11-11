using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class SubscriptionRefresh
{
    public static async Task<Settings?> RunAsync(Settings snapshot, bool manual, CancellationToken token,
        Func<SubscriptionDefinition, string, CancellationToken, Task<string>>? download = null, Action<string>? log = null)
    {
        var definition = ConfigurationSubscription.Get(snapshot);
        if (definition is null || !manual && !ConfigurationSubscription.Due(snapshot, DateTimeOffset.UtcNow)) return null;
        int source = 0;
        foreach (string url in definition.Sources)
        {
            token.ThrowIfCancellationRequested();
            string stage = "download";
            try
            {
                string text = await (download ?? DownloadAsync)(definition, url, token).ConfigureAwait(false);
                stage = "parse";
                var import = ProfileTransfer.Parse(text);
                stage = "apply";
                return ConfigurationSubscription.ApplySnapshot(snapshot, import, source, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OverflowException)
            {
                string reason = error switch
                {
                    HttpRequestException { StatusCode: { } code } => "HTTP " + (int)code,
                    HttpRequestException http => "HTTP transport " + http.HttpRequestError,
                    OperationCanceledException => "timeout",
                    _ => error.GetType().Name
                };
                // Exception messages may contain credentials, source URLs or configuration contents.
                log?.Invoke($"Configuration subscription source {source + 1} failed during {stage}: {reason}.");
            }
            source++;
        }
        return ConfigurationSubscription.Failed(snapshot, DateTimeOffset.UtcNow);
    }

    public static Task<string> DownloadAsync(SubscriptionDefinition definition, string url, CancellationToken token) => DownloadAsync(definition, url, token,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip });

    public static async Task<string> DownloadAsync(SubscriptionDefinition definition, string url, CancellationToken token, HttpMessageHandler handler)
    {
        _ = SubscriptionDefinition.NormalizeUrl(url);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return await DownloadAsync(definition, url, token, client).ConfigureAwait(false);
    }

    public static async Task<string> DownloadAsync(SubscriptionDefinition definition, string url, CancellationToken token, HttpClient client)
    {
        _ = SubscriptionDefinition.NormalizeUrl(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-MegaProxy-Client", "windows");
        string version = typeof(SubscriptionRefresh).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        request.Headers.Add("X-MegaProxy-Version", version);
        if (definition.Username is not null || definition.Password is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((definition.Username ?? "") + ":" + (definition.Password ?? ""))));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Configuration source returned an unsuccessful status.", null, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (true)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            read.CancelAfter(TimeSpan.FromSeconds(15));
            int count = await body.ReadAsync(buffer, read.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > PortableConfiguration.MaximumBytes) throw new IOException("Configuration source exceeds 4 MiB.");
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
