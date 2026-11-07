using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed record PodkopList(string Id, string Name, string[] Paths);

public static class PodkopCatalog
{
    public const string BaseUrl = "https://raw.githubusercontent.com/itdoginfo/allow-domains/main/";
    public static IReadOnlyList<PodkopList> Lists { get; } = Create();
    private static PodkopList[] Create()
    {
        var result = new List<PodkopList>
        {
            new("russia_inside", "Russia inside", ["Russia/inside-raw.lst"]),
            new("russia_outside", "Russia outside", ["Russia/outside-raw.lst"]),
            new("ukraine_inside", "Ukraine inside", ["Ukraine/inside-raw.lst"])
        };
        foreach (string id in new[] { "geoblock", "block", "porn", "news", "anime", "hodca" })
            result.Add(new(id, id == "hodca" ? "H.O.D.C.A" : id, ["Categories/" + id + ".lst"]));
        foreach (string id in new[] { "youtube", "discord", "meta", "twitter", "hdrezka", "tiktok", "telegram", "cloudflare", "google_ai", "google_play", "roblox", "hetzner", "ovh", "digitalocean", "cloudfront", "google_meet" })
        {
            var paths = new List<string> { "Services/" + id + ".lst" };
            if (id is "discord" or "meta" or "twitter" or "telegram" or "cloudflare" or "roblox" or "hetzner" or "ovh" or "digitalocean" or "cloudfront" or "google_meet")
            {
                paths.Add("Subnets/IPv4/" + id + ".lst");
                if (id != "roblox") paths.Add("Subnets/IPv6/" + id + ".lst");
            }
            result.Add(new(id, id.Replace('_', ' '), paths.ToArray()));
        }
        return result.ToArray();
    }

    public static string[] Parse(string destination)
    {
        if (!destination.StartsWith("podkop:", StringComparison.Ordinal)) return [];
        var ids = destination[7..].Split(',').Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length is < 1 or > 32 || ids.Any(id => !Lists.Any(list => list.Id == id))) throw new ArgumentException("Select valid Podkop community lists.");
        return ids;
    }

    public static string[] Used(Settings settings) => settings.Rules.Where(rule => rule.Enabled).SelectMany(rule => Parse(rule.Destination.Trim())).Distinct(StringComparer.Ordinal).ToArray();
    public static JsonObject Definition(string id) => new() { ["type"] = "local", ["tag"] = "podkop:" + id, ["format"] = "source", ["path"] = "podkop/" + id + ".json" };
}
