using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class IpReputation
{
    private static readonly Dictionary<string, (DateTimeOffset Saved, string Verdict)> Cache = new();

    public static string Parse(int status, string body, string address)
    {
        if (body.Length > 65536) return "Unknown response format";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "Unknown response format";
            if (status == 429)
            {
                string message = root.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()!.ToLowerInvariant() : "";
                return new[] { "exhausted", "daily", "allowance" }.Any(message.Contains) ? "ProxyCheck quota exceeded" : "ProxyCheck rate limit reached";
            }
            if (status is < 200 or >= 300) return "Reputation request failed";
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String ||
                state.GetString() is not ("ok" or "warning") ||
                !root.TryGetProperty(address, out var entry) || entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("detections", out var flags) || flags.ValueKind != JsonValueKind.Object)
                return "Unknown response format";
            var values = new List<bool>();
            foreach (string key in new[] { "vpn", "proxy", "tor", "anonymous" })
                if (flags.TryGetProperty(key, out var value))
                {
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "Unknown response format";
                    values.Add(value.GetBoolean());
                }
            return values.Count == 0 ? "Unknown response format" : values.Any(value => value)
                ? "VPN/proxy detected" : "VPN/proxy not detected";
        }
        catch (JsonException) { return "Unknown response format"; }
    }

    public static async Task<string> CheckAsync(HttpClient client, string address, Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (NetworkDiagnostics.ParseIp(address) is not { } ip) return "Exit IP unavailable";
        lock (Cache)
            if (Cache.TryGetValue(ip, out var cached) && DateTimeOffset.UtcNow >= cached.Saved &&
                DateTimeOffset.UtcNow - cached.Saved < TimeSpan.FromDays(1)) return cached.Verdict;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync("https://proxycheck.io/v3/" + Uri.EscapeDataString(ip) + "?p=0&tag=0",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            string result = Parse((int)response.StatusCode, await NetworkDiagnostics.ReadBoundedAsync(response.Content, 65536, timeout.Token), ip);
            log("ProxyCheck: " + result + ".");
            if (result is "VPN/proxy detected" or "VPN/proxy not detected")
                lock (Cache)
                {
                    if (Cache.Count >= 128) Cache.Remove(Cache.MinBy(item => item.Value.Saved).Key);
                    Cache[ip] = (DateTimeOffset.UtcNow, result);
                }
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            log("ProxyCheck request failed: " + error.GetType().Name + ".");
            return "Reputation request failed";
        }
    }
}
