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
        Check(calls.SequenceEqual(definition.Sources) && updated!.Profiles.Count == 2 && updated.ActiveProfileId == local.Id && ConfigurationSubscription.SourceIndex(updated) == 1, "Subscription retries malformed primary, records the successful source and preserves local selection.");
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
        string waiting = await cache.GetAsync("youtube", CancellationToken.None,
            (_, _) => throw new Exception("Proxy-only bootstrap must not access the network."), cacheOnly: true);
        Check(cache.Unavailable.Contains("youtube") && File.Exists(waiting), "Missing proxy-only lists stay inactive until the connection is available.");
        int skipped = 0;
        var suffixes = PodkopCache.Parse(new[] { ".ua\n*.example.org\ninvalid/path\n" }, count => skipped = count);
        Check(skipped == 1 && suffixes["rules"]![0]!["domain_suffix"]!.AsArray().Select(value => value!.ToString()).SequenceEqual(new[] { "ua", "example.org" }), "Podkop accepts leading-dot/wildcard suffixes and reports malformed lines without dropping valid entries.");
        var deduplicated = PodkopCache.Merge(new[]
        {
            PodkopCache.Parse(new[] { "example.com\n192.0.2.0/24\n2001:db8::/32" }),
            PodkopCache.Parse(new[] { ".EXAMPLE.com\napi.example.com\n192.0.2.0/24\n2001:0db8:0:0::/32" })
        });
        Check(deduplicated["rules"]![0]!["domain_suffix"]!.AsArray().Count == 2 && deduplicated["rules"]![1]!["ip_cidr"]!.AsArray().Count == 2,
            "Combined lists deduplicate domains, normalized suffixes and equivalent IPv4/IPv6 subnets.");
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
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => throw new Exception("An expired proxy-only cache must remain usable offline."), cacheOnly: true);
        Check(File.ReadAllText(path) == before, "Proxy-only bootstrap retains even an expired verified cache.");
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => Task.FromResult("https://invalid.example/path"));
        Check(File.ReadAllText(path) == before, "Invalid list updates retain the last verified copy.");
        await cache.GetAsync("discord", CancellationToken.None, (_, _) => throw new HttpRequestException("offline"));
        string unavailable = await cache.GetAsync("youtube", CancellationToken.None, (_, _) => throw new IOException("offline"));
        Check(cache.Unavailable.Contains("youtube") && JsonNode.Parse(File.ReadAllText(unavailable))!["rules"]!.AsArray().Count == 0, "Missing first-download sources become visibly inactive instead of aborting the connection.");
        var proxy = ProxyProfile.Create("Rule set check"); proxy.Proxy["host"] = "proxy.example";
        var rules = new Settings { Profiles = [proxy], ActiveProfileId = proxy.Id, Rules = [new() { Destination = "podkop:discord,youtube" }] };
        var config = Configuration.Build(rules, 1, "test");
        string combined = cache.MergeGroup(["discord", "youtube"]);
        Check(combined == cache.MergeGroup(["youtube", "discord"]), "List order does not create duplicate combined files.");
        config["route"]!["rule_set"]![0]!["path"] = combined;
        var emptySet = PodkopCatalog.Definition("youtube"); emptySet["path"] = unavailable;
        config["route"]!["rule_set"]!.AsArray().Add(emptySet);
        string configPath = Path.Combine(directory, "check.json"); File.WriteAllText(configPath, config.ToJsonString());
        foreach (string core in new[] { ".tools/sing-box/sing-box.exe", ".tools/sing-box-h3/sing-box.exe" })
        {
            if (!File.Exists(core)) continue;
            var start = new ProcessStartInfo(Path.GetFullPath(core)); start.ArgumentList.Add("check"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configPath);
            var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(15), CancellationToken.None);
            Check(result.ExitCode == 0, "Both cores accept the generated Podkop source/routing configuration: " + result.Error);
        }
        await cache.GetAsync("youtube", CancellationToken.None, (_, _) => Task.FromResult("youtube.com"));
        cache.MergeGroup(["discord", "youtube"]);
        Check(!cache.Unavailable.Contains("youtube") && JsonNode.Parse(File.ReadAllText(unavailable))!["rules"]!.AsArray().Count > 0, "A previously inactive source is retried and recovers automatically.");
        Check(JsonNode.Parse(File.ReadAllText(combined))!["rules"]![0]!["domain_suffix"]!.AsArray().Count == 3, "Combined sets are rebuilt when their components recover.");
        Console.WriteLine("Subscription fallback/protocol/bounds and verified Podkop cache checks passed; no TUN.");
    }
}
