using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

internal static class SniffChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static JsonObject Build(params RoutingRule[] rules) => Configuration.Build(new Settings { Rules = [.. rules], BypassLan = false }, 9999, "synthetic");
        static JsonArray Rules(JsonObject config) => config["route"]!["rules"]!.AsArray();
        static int Sniffs(JsonObject config) => Rules(config).Count(rule => rule!["action"]?.ToString() == "sniff");
        var plain = Build();
        Check(Sniffs(plain) == 0 && Rules(plain)[0]!["port"]!.GetValue<int>() == 53, "No sniff without domain rules; DNS keeps its direct route.");
        Check(Sniffs(Build(new() { Source = "folder", Path = @"C:\Apps", Action = "system" },
            new() { Destination = "192.0.2.0/24", Action = "block" }, new() { Destination = "2001:db8::1", Action = "block" },
            new() { Destination = "*.example.com", Enabled = false })) == 0, "EXE/folder/IP and disabled domain rules require no sniffing.");
        var domain = new RoutingRule { Destination = "blocked.example", Action = "block" };
        var global = Build(domain, new() { Destination = "*.example.org", Action = "system" });
        Check(Sniffs(global) == 1, "One domain sniff serves later rules with the same source.");
        var sniff = Rules(global).Single(rule => rule!["action"]?.ToString() == "sniff")!;
        Check(sniff["sniffer"]!.AsArray().Select(x => x!.ToString()).SequenceEqual(new[] { "tls", "http", "quic" }) && sniff["domain_regex"] is null, "Inspect domain-bearing protocols even with a potentially ambiguous DNS mapping.");
        var scoped = Build(new RoutingRule { Source = "exe", Path = @"C:\Synthetic\Only.exe", Destination = domain.Destination, Action = "block" });
        Check(Rules(scoped)[2]!["process_path_regex"] is JsonArray, "Domain sniff must retain the application constraint.");
        var early = Build(new() { Source = "all", Destination = "127.0.0.1", Action = "system" }, domain);
        Check(Rules(early)[2]!["ip_cidr"] is JsonArray && Rules(early)[3]!["action"]!.ToString() == "sniff", "Earlier terminal routes remain before sniffing.");
        var mixed = Build(new() { Source = "folder", Path = @"C:\Apps", Destination = domain.Destination, Action = "block" }, domain,
            new() { Source = "exe", Path = @"C:\Another.exe", Destination = domain.Destination, Action = "block" });
        Check(Sniffs(mixed) == 2, "A global sniff covers later application-specific rules, but not vice versa.");
        Check(Sniffs(Build(new RoutingRule { Destination = "podkop:youtube", Action = "block" })) == 1, "Podkop domain lists retain sniffing.");
        var profile = ProxyProfile.Create("Synthetic"); profile.Proxy["host"] = "proxy.example";
        var proxy = Configuration.Build(new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" }, 9999, "synthetic");
        Check(Sniffs(proxy) == 0 && Rules(proxy)[0]!["action"]!.ToString() == "hijack-dns", "Proxy-default DNS works without a catch-all sniffer.");
        Directory.CreateDirectory("artifacts/sniff-checks");
        foreach (var (name, config) in new[] { ("plain", plain), ("global", global), ("scoped", scoped), ("early", early), ("proxy", proxy) })
            File.WriteAllText($"artifacts/sniff-checks/{name}.json", config.ToJsonString(Configuration.JsonOptions));
    }
}
