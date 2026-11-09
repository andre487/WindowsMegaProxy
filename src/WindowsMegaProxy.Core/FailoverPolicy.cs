using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class FailoverPolicy
{
    public static string Mode(Settings settings) => settings.SharedOptions["failover"]?["mode"]?.GetValue<string>() ?? "DISABLED";
    public static string[] Selected(Settings settings) => settings.SharedOptions["failover"]?["profileIds"] is JsonArray ids ? ids.Select(id => id?.GetValue<string>() ?? throw new ArgumentException("Invalid failover profile ID.")).ToArray() : [];
    public static void Validate(Settings settings)
    {
        if (Mode(settings) is not ("DISABLED" or "SELECTED" or "ALL")) throw new ArgumentException("Unknown failover mode.");
        var ids = Selected(settings);
        if (ids.Length > 1000 || ids.Any(id => id.Length is < 1 or > 256) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Invalid failover profile IDs.");
    }
    public static IEnumerable<ProxyProfile> Candidates(Settings settings, string? active, ISet<string> attempted)
    {
        Validate(settings);
        if (Mode(settings) == "DISABLED") yield break;
        int index = settings.Profiles.FindIndex(profile => profile.Id == active);
        var ordered = settings.Profiles.Skip(index + 1).Concat(settings.Profiles.Take(index + 1));
        var selected = Selected(settings).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in ordered)
            if (profile.Id != active && !attempted.Contains(profile.Id) && profile.Supported && (Mode(settings) == "ALL" || selected.Contains(profile.Id)))
                yield return profile;
    }
    public static bool TransportFailure(string message)
    {
        string text = message.ToLowerInvariant();
        if (new[] { "certificate", "x509", "host key", "unauthorized", "authentication", "407", "403", "invalid config" }.Any(text.Contains)) return false;
        return new[] { "timeout", "timed out", "context deadline exceeded", "connection refused", "connection reset", "network is unreachable", "no route to host", "no such host", "connectex", "wsarecv", "unexpected eof" }.Any(text.Contains);
    }
    public static JsonNode? Signature(Settings settings) => Mode(settings) == "DISABLED" ? null : new JsonObject
    {
        ["options"] = settings.SharedOptions["failover"]?.DeepClone(),
        ["profiles"] = new JsonArray(settings.Profiles.Where(profile => profile.Supported && (Mode(settings) == "ALL" || Selected(settings).Contains(profile.Id)))
            .Select(profile => (JsonNode)new JsonObject { ["id"] = profile.Id, ["proxy"] = profile.Proxy.DeepClone(), ["dns"] = profile.Data["dns"]?.DeepClone(), ["routing"] = profile.Data["routing"]?.DeepClone() }).ToArray())
    };
}
