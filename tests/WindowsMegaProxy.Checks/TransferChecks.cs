using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

internal static class TransferChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { return; }
        throw new Exception("Invalid transfer was accepted.");
    }
    private static ProxyProfile Profile(string id, string host)
    {
        var profile = ProxyProfile.Create(id); profile.Data["id"] = id; profile.Proxy["host"] = host;
        return profile;
    }
    public static void Run()
    {
        var list = ProfileTransfer.Parse("# superproxy:proxylist:v1\nhttps://a%2Bb:p%3A%2B@proxy.example:8443?title=Test+proxy&cc=de\nsocks5://[::1]:1080\nmasque://:@h3.example\nhttp://ignored.example\n");
        Check(!list.Canonical && list.Skipped == 1 && list.Profiles.Count == 3, "Import SuperProxy/ProxyList and report unsupported schemes.");
        Check(list.Profiles[0].Name == "Test proxy" && list.Profiles[0].CountryCode == "DE" && list.Profiles[0].Proxy["username"]!.ToString() == "a+b" && list.Profiles[0].Proxy["password"]!.ToString() == "p:+", "URI credentials and query encoding have different plus semantics.");
        Check(list.Profiles[1].Host == "::1" && list.Profiles[2].Type == "MASQUE", "Import IPv6 and MASQUE proxy URLs.");
        var foxy = ProfileTransfer.Parse("""{"data":[{"type":"ssl","hostname":"proxy.example","port":"8443","title":"Foxy","username":"a","password":"b"},{"type":"http","hostname":"skip.example"}]}""");
        Check(foxy.Profiles.Single().Type == "HTTPS" && foxy.Profiles[0].Port == 8443 && foxy.Skipped == 1, "Import FoxyProxy ssl alias and numeric strings.");
        var first = Profile("first", "one.example"); first.Proxy["username"] = "user"; first.Proxy["password"] = "secret";
        first.Proxy["knockHost"] = "knock.example";
        var second = Profile("second", "two.example");
        var current = new Settings { Profiles = [first, second], ActiveProfileId = first.Id };
        for (int version = 1; version <= 8; version++)
        {
            var root = PortableConfiguration.Export(current);
            root["version"] = version;
            if (version < 7) { root["profiles"]![0]!.AsObject().Remove("id"); root.Remove("activeProfileId"); }
            var parsed = ProfileTransfer.Parse(root.ToJsonString());
            Check(PortableConfiguration.Import(parsed.Document.ToJsonString(), new Settings()).Profiles.Count == 2, "Import every supported MegaProxy schema version.");
        }
        var exportedList = ProfileTransfer.ExportProxyList(current, false);
        Check(!exportedList.Contains("secret") && ProfileTransfer.Parse(exportedList).Profiles.Count == 2, "Export interoperable proxy lists without passwords by default.");
        Check(ProfileTransfer.Parse(ProfileTransfer.ExportProxyList(current, true)).Profiles[0].Proxy["password"]!.ToString() == "secret", "ProxyList password opt-in round trip.");
        var incoming = PortableConfiguration.Export(new Settings { Profiles = [first.Copy()], ActiveProfileId = first.Id, Theme = "dark" });
        incoming["profiles"]![0]!["proxy"]!["port"] = 8443;
        incoming["profiles"]![0]!["proxy"]!["allowInvalidProxyCertificate"] = true;
        var review = new ImportReview(ProfileTransfer.Parse(incoming.ToJsonString()), current);
        Check(review.Options.Any(option => option.Group == "Connection") && review.Options.Any(option => option.Group == "Security") && review.UnsafeTrust, "Review changed option groups and unsafe trust flags.");
        var kept = review.Apply(new HashSet<string>(), false, new HashSet<string>());
        Check(kept.Profiles.Count == 2 && kept.Profiles[0].Port == 443 && kept.Profiles[0].Proxy["password"]!.ToString() == "secret" && kept.Theme == "auto", "Unchecked settings and omitted secrets stay local.");
        var replaced = review.Apply(new HashSet<string> { "first:Connection" }, true, new HashSet<string> { "second" });
        Check(replaced.Profiles.Count == 1 && replaced.Profiles[0].Port == 8443 && replaced.Profiles[0].Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() != true && replaced.Profiles[0].Proxy["password"] is null && replaced.Theme == "dark", "Apply selected options, preserve trust and clear credentials at changed endpoints.");
        Reject(() => review.Apply(new HashSet<string>(), false, new HashSet<string> { "first" }));

        var bootstrap = PortableConfiguration.Export(current, true);
        bootstrap["subscription"] = new JsonObject { ["url"] = "https://feed.example/config?token=secret", ["fallbackUrls"] = new JsonArray("https://backup.example/config"), ["username"] = "user", ["password"] = "feed-secret", ["intervalMinutes"] = 60, ["enabled"] = false };
        var subscribed = new ImportReview(ProfileTransfer.Parse(bootstrap.ToJsonString()), new Settings()).Apply(new HashSet<string>(), true, new HashSet<string>());
        Check(ConfigurationSubscription.OwnedIds(subscribed).SetEquals(new[] { "first", "second" }), "Imported subscription adopts imported profiles.");
        var local = Profile("local", "local.example"); subscribed.Profiles.Add(local);
        var feed = PortableConfiguration.Export(new Settings { Profiles = [first.Copy()], ActiveProfileId = first.Id });
        feed["subscription"] = new JsonObject { ["url"] = "https://replacement.example/new", ["username"] = "remote", ["password"] = "remote-secret", ["intervalMinutes"] = 1, ["enabled"] = true };
        var refreshed = ConfigurationSubscription.ApplySnapshot(subscribed, ProfileTransfer.Parse(feed.ToJsonString()), 1, DateTimeOffset.UtcNow);
        var definition = ConfigurationSubscription.Get(refreshed)!;
        Check(refreshed.Profiles.Select(profile => profile.Id).SequenceEqual(new[] { "first", "local" }) && refreshed.ActiveProfileId == "first", "Feeds replace owned profiles and preserve local profiles/selections.");
        Check(definition.Url == "https://replacement.example/new" && definition.FallbackUrls.Length == 0 && definition.Username == "user" && definition.Password == "feed-secret" && definition.IntervalMinutes == 60 && !definition.Enabled, "Feeds replace source URLs atomically while retaining local auth, interval and pause state.");
        Check(PortableConfiguration.Export(refreshed)["windows"]![ConfigurationSubscription.StateKey] is null, "Ownership/status are never exported.");
        Check(PortableConfiguration.Export(refreshed)["subscription"]!["password"] is null, "Subscription passwords follow the export password option.");
        var collision = PortableConfiguration.Export(new Settings { Profiles = [local.Copy()], ActiveProfileId = local.Id });
        collision["profiles"]![0]!["proxy"]!["port"] = 8443;
        var adopted = ConfigurationSubscription.ApplySnapshot(subscribed, ProfileTransfer.Parse(collision.ToJsonString()), 0, DateTimeOffset.UtcNow);
        Check(adopted.Profiles.Single(profile => profile.Id == local.Id).Port == 8443 && ConfigurationSubscription.OwnedIds(adopted).Contains(local.Id),
            "A feed replaces a matching local ID and adopts it for future refreshes.");
        var unowned = subscribed.Copy();
        unowned.WindowsOptions.Remove(ConfigurationSubscription.StateKey);
        var recovered = ConfigurationSubscription.ApplySnapshot(unowned, ProfileTransfer.Parse(collision.ToJsonString()), 0, DateTimeOffset.UtcNow);
        Check(recovered.Profiles.Count == unowned.Profiles.Count && recovered.Profiles.Single(profile => profile.Id == local.Id).Port == 8443 &&
            ConfigurationSubscription.OwnedIds(recovered).SetEquals(new[] { local.Id }),
            "Missing ownership is recovered for matching IDs without deleting unrelated local profiles.");
        var nextFeed = PortableConfiguration.Export(new Settings { Profiles = [first.Copy()], ActiveProfileId = first.Id });
        var nextRefresh = ConfigurationSubscription.ApplySnapshot(recovered, ProfileTransfer.Parse(nextFeed.ToJsonString()), 0, DateTimeOffset.UtcNow);
        Check(nextRefresh.Profiles.All(profile => profile.Id != local.Id) && nextRefresh.Profiles.Any(profile => profile.Id == "second"),
            "Later refreshes remove adopted profiles absent from the feed while keeping unrelated local profiles.");
        var badSubscription = (JsonObject)feed.DeepClone(); badSubscription["subscription"] = new JsonArray("invalid");
        Reject(() => ConfigurationSubscription.ApplySnapshot(subscribed, ProfileTransfer.Parse(badSubscription.ToJsonString()), 0, DateTimeOffset.UtcNow));
        var badBackups = (JsonObject)feed.DeepClone(); badBackups["failover"] = new JsonObject { ["mode"] = "SELECTED", ["profileIds"] = new JsonArray((JsonNode?)null) };
        Reject(() => ConfigurationSubscription.ApplySnapshot(subscribed, ProfileTransfer.Parse(badBackups.ToJsonString()), 0, DateTimeOffset.UtcNow));
        var unchanged = ConfigurationSubscription.Failed(subscribed, DateTimeOffset.UtcNow);
        Check(unchanged.Profiles.Count == subscribed.Profiles.Count && ConfigurationSubscription.Status(unchanged) == "Update failed", "Failed snapshots preserve working configuration.");
        foreach (string url in new[] { "http://feed.example/config", "https://user:pass@feed.example/config", "https://feed.example/config#token", "https://feed.example/a b" })
            Reject(() => SubscriptionDefinition.NormalizeUrl(url));
        var duplicates = new SubscriptionDefinition("https://feed.example/config", ["https://FEED.example:443/config"], null, null, 60, true);
        Reject(() => SubscriptionDefinition.Parse(duplicates.ToJson()));
        var noSubscription = PortableConfiguration.Export(current);
        var retained = new ImportReview(ProfileTransfer.Parse(noSubscription.ToJsonString()), subscribed).Apply(new HashSet<string>(), false, new HashSet<string>());
        Check(ConfigurationSubscription.Get(retained) is not null && ConfigurationSubscription.OwnedIds(retained).Count == 2, "Omitted subscription preserves local ownership and settings.");
        noSubscription["subscription"] = null;
        var removed = new ImportReview(ProfileTransfer.Parse(noSubscription.ToJsonString()), subscribed).Apply(new HashSet<string>(), false, new HashSet<string>());
        Check(ConfigurationSubscription.Get(removed) is null && removed.Profiles.Count == 3, "Explicit null subscription removes ownership and keeps profiles.");

        current.SharedOptions["failover"] = new JsonObject { ["mode"] = "SELECTED", ["profileIds"] = new JsonArray("second") };
        Check(FailoverPolicy.Candidates(current, "first", new HashSet<string>()).Single().Id == "second", "Selected failover respects allowed profiles.");
        Check(!FailoverPolicy.Candidates(current, "first", new HashSet<string> { "second" }).Any(), "Failover is bounded to unattempted profiles.");
        Check(FailoverPolicy.TransportFailure("outbound/http[proxy]: connection reset") && !FailoverPolicy.TransportFailure("x509 certificate timeout") && !FailoverPolicy.TransportFailure("407 authentication timeout"), "Trust and authentication errors cannot trigger failover.");
        string before = Configuration.Signature(current);
        current.Profiles[1].Proxy["host"] = "updated.example";
        Check(Configuration.Signature(current) != before, "Backup network changes require reconnecting.");
        current.Rules.Add(new RoutingRule { Destination = "podkop:youtube,discord", Action = "proxy" });
        var config = Configuration.Build(current, 1, "test");
        Check(config["route"]!["rule_set"]!.AsArray().Count == 1 && Configuration.BuildRule(current.Rules[0])["rule_set"]!.AsArray().Count == 1, "Multiple Podkop lists share a deduplicated native rule set.");
        current.Rules.Add(new RoutingRule { Destination = "podkop:discord,youtube", Action = "block" });
        Check(Configuration.Build(current, 1, "test")["route"]!["rule_set"]!.AsArray().Count == 1, "Equivalent selections reuse the same set without changing rule actions or priority.");
        Reject(() => Configuration.BuildRule(new RoutingRule { Destination = "podkop:../../bad" }));
        Console.WriteLine("Configuration formats, import review, subscriptions, failover policy and Podkop rules passed.");
    }
}
