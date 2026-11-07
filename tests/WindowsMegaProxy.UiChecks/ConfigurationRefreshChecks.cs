using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class ConfigurationRefreshChecks
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return; }
        throw new Exception("Invalid source was accepted.");
    }
    public static async Task RunAsync()
    {
        var definition = new SubscriptionDefinition("https://feed.example/config?token=private", ["https://backup.example/config"], "user", "secret", 60, true);
        var settings = new Settings(); ConfigurationSubscription.Configure(settings, definition);
        var local = ProxyProfile.Create("Local"); local.Proxy["host"] = "local.example";
        settings.Profiles.Add(local); settings.ActiveProfileId = local.Id;
        var calls = new List<string>();
        var updated = await SubscriptionRefresh.RunAsync(settings, false, CancellationToken.None, (_, url, _) =>
        {
            calls.Add(url);
            return Task.FromResult(url == definition.Url ? "not a proxy list" : "socks5://feed.example:1080?title=Feed");
        });
        Check(calls.SequenceEqual(definition.Sources) && updated!.Profiles.Count == 2 && updated.ActiveProfileId == local.Id, "Subscription retries malformed primary and preserves local selection.");
        string ownedId = ConfigurationSubscription.OwnedIds(updated!).Single();
        var second = await SubscriptionRefresh.RunAsync(updated!, true, CancellationToken.None, (_, _, _) => Task.FromResult("socks5://feed.example:1080?title=Feed"));
        Check(ConfigurationSubscription.OwnedIds(second!).Single() == ownedId && second!.Profiles.Count == 2, "Text subscriptions reuse stable owned IDs.");
        var notDue = await SubscriptionRefresh.RunAsync(second!, false, CancellationToken.None, (_, _, _) => throw new Exception("Not due yet"));
        Check(notDue is null, "Automatic refresh respects interval.");
        var failed = await SubscriptionRefresh.RunAsync(second!, true, CancellationToken.None, (_, _, _) => throw new IOException("private remote URL/body"));
        Check(failed!.Profiles.Count == 2 && ConfigurationSubscription.Status(failed) == "Update failed", "All failed sources preserve working profiles and expose generic status.");
        using var stopping = new CancellationTokenSource(); stopping.Cancel();
        try { await SubscriptionRefresh.RunAsync(settings, true, stopping.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        string body = await SubscriptionRefresh.DownloadAsync(definition, definition.Url, CancellationToken.None, new Handler(request =>
        {
            Check(request.Headers.GetValues("X-MegaProxy-Client").Single() == "windows" && request.Headers.Contains("X-MegaProxy-Version"), "Subscription protocol identifies the Windows client/version.");
            Check(request.Headers.Authorization?.ToString() == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:secret")) && !request.Headers.Contains("Cookie"), "Only explicit subscription credentials are sent.");
            return new(HttpStatusCode.OK) { Content = new StringContent("socks5://feed.example") };
        }));
        Check(body == "socks5://feed.example", "Read a bounded UTF-8 response.");
        await RejectAsync(() => SubscriptionRefresh.DownloadAsync(definition, definition.Url, CancellationToken.None, new Handler(_ =>
            new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.example/") }, Content = new StringContent("private body") })));
        await RejectAsync(() => SubscriptionRefresh.DownloadAsync(definition, definition.Url, CancellationToken.None, new Handler(_ =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[PortableConfiguration.MaximumBytes + 1]) })));

        string directory = Path.GetFullPath(Path.Combine("artifacts/configuration-checks", Guid.NewGuid().ToString("N")));
        var cache = new PodkopCache(directory);
        int downloads = 0;
        string path = await cache.GetAsync("discord", CancellationToken.None, (url, _) =>
        {
            downloads++;
            Check(url.StartsWith(PodkopCatalog.BaseUrl, StringComparison.Ordinal), "Only catalog-owned official URLs can populate Podkop cache.");
            return Task.FromResult(url.Contains("Subnets") ? "192.0.2.0/24\n2001:db8::/32" : "# comment\r\nexample.com\napi.example.com\nexample.com");
        });
        var parsed = JsonNode.Parse(File.ReadAllText(path))!;
        Check(parsed["rules"]!.AsArray().Count == 2 && downloads == 3, "Combine/deduplicate domains and both IP families into native source rule sets.");
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => throw new Exception("Fresh cache must avoid network"));
        string before = File.ReadAllText(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => Task.FromResult("https://invalid.example/path"));
        Check(File.ReadAllText(path) == before, "Invalid list updates retain the last verified copy.");
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => throw new HttpRequestException("offline"));
        await RejectAsync(() => cache.GetAsync("youtube", CancellationToken.None, (_, _) => throw new IOException("offline")));
        var proxy = ProxyProfile.Create("Rule set check"); proxy.Proxy["host"] = "proxy.example";
        var rules = new Settings { Profiles = [proxy], ActiveProfileId = proxy.Id, Rules = [new() { Destination = "podkop:discord" }] };
        var config = Configuration.Build(rules, 1, "test");
        config["route"]!["rule_set"]![0]!["path"] = path;
        string configPath = Path.Combine(directory, "check.json"); File.WriteAllText(configPath, config.ToJsonString());
        foreach (string core in new[] { ".tools/sing-box/sing-box.exe", ".tools/sing-box-h3/sing-box.exe" })
        {
            if (!File.Exists(core)) continue;
            var start = new ProcessStartInfo(Path.GetFullPath(core)); start.ArgumentList.Add("check"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(15), CancellationToken.None);
            Check(result.ExitCode == 0, "Both cores accept the generated Podkop source/routing configuration: " + result.Error);
        }
        Console.WriteLine("Subscription fallback/protocol/bounds and verified Podkop cache checks passed; no TUN.");
    }
}
