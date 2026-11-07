using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed record SubscriptionDefinition(string Url, string[] FallbackUrls, string? Username, string? Password, int IntervalMinutes, bool Enabled)
{
    public IEnumerable<string> Sources => new[] { Url }.Concat(FallbackUrls);
    public static string NormalizeUrl(string value)
    {
        if (value.Length is < 1 or > 2048 || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host.Length == 0 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Port is < 1 or > 65535)
            throw new InvalidDataException("Subscription sources must be final HTTPS URLs without credentials or fragments.");
        return uri.AbsoluteUri;
    }

    public static SubscriptionDefinition Parse(JsonObject value)
    {
        string? String(string key) => value.ContainsKey(key) ? value[key]?.GetValue<string>() ?? throw new InvalidDataException("Use an empty string to clear subscription credentials.") : null;
        var urls = !value.ContainsKey("fallbackUrls") ? [] : value["fallbackUrls"]?.AsArray().Select(item => item?.GetValue<string>() ?? throw new InvalidDataException("Invalid backup URL.")).ToArray() ?? throw new InvalidDataException("Invalid backup URL list.");
        var result = new SubscriptionDefinition(String("url") ?? "", urls, String("username"), String("password"),
            value.ContainsKey("intervalMinutes") ? value["intervalMinutes"]?.GetValue<int>() ?? throw new InvalidDataException("Invalid subscription interval.") : 60,
            value.ContainsKey("enabled") ? value["enabled"]?.GetValue<bool>() ?? throw new InvalidDataException("Invalid subscription pause state.") : true);
        if (urls.Length > 7 || result.IntervalMinutes is < 1 or > 10080 || result.Sources.Select(NormalizeUrl).Distinct(StringComparer.Ordinal).Count() != urls.Length + 1 ||
            result.Username is { } username && (username.Length > 1024 || username.Contains(':') || username.Any(char.IsControl)) ||
            result.Password is { } password && (password.Length > 1024 || password.Any(char.IsControl)))
            throw new InvalidDataException("Invalid configuration subscription.");
        return result;
    }

    public JsonObject ToJson()
    {
        var result = new JsonObject { ["url"] = Url, ["fallbackUrls"] = new JsonArray(FallbackUrls.Select(url => (JsonNode)JsonValue.Create(url)!).ToArray()),
            ["intervalMinutes"] = IntervalMinutes, ["enabled"] = Enabled };
        if (Username is not null) result["username"] = Username;
        if (Password is not null) result["password"] = Password;
        return result;
    }

    public bool SameAccount(SubscriptionDefinition other) => NormalizeUrl(Url) == NormalizeUrl(other.Url) && (Username ?? "") == (other.Username ?? "");
}

public static class ConfigurationSubscription
{
    public const string StateKey = "_subscription";
    public static SubscriptionDefinition? Get(Settings settings) => settings.SharedOptions["subscription"] is null ? null
        : settings.SharedOptions["subscription"] is JsonObject value ? SubscriptionDefinition.Parse(value) : throw new InvalidDataException("Invalid subscription object.");
    private static JsonObject State(Settings settings) => settings.WindowsOptions[StateKey] as JsonObject ?? new JsonObject();
    public static HashSet<string> OwnedIds(Settings settings) => State(settings)["ownedIds"] is JsonArray ids ? ids.Select(id => id?.GetValue<string>() ?? throw new InvalidDataException("Invalid subscription ownership metadata.")).ToHashSet(StringComparer.Ordinal) : [];
    public static bool Due(Settings settings, DateTimeOffset now) => Get(settings) is { Enabled: true } subscription &&
        now.ToUnixTimeSeconds() - (State(settings)["lastAttempt"]?.GetValue<long>() ?? 0) >= subscription.IntervalMinutes * 60L;
    public static string Status(Settings settings) => State(settings)["status"]?.ToString() ?? "Not checked";
    public static long LastSuccess(Settings settings) => State(settings)["lastSuccess"]?.GetValue<long>() ?? 0;
    public static int SourceIndex(Settings settings) => State(settings)["sourceIndex"]?.GetValue<int>() ?? -1;

    public static void Configure(Settings settings, SubscriptionDefinition? definition)
    {
        var previous = Get(settings);
        if (definition is null) { settings.SharedOptions.Remove("subscription"); settings.WindowsOptions.Remove(StateKey); return; }
        _ = SubscriptionDefinition.Parse(definition.ToJson());
        if (JsonNode.DeepEquals(settings.SharedOptions["subscription"], definition.ToJson())) return;
        var state = previous is not null && definition.SameAccount(previous) ? (JsonObject)State(settings).DeepClone() : new JsonObject();
        state["generation"] = Guid.NewGuid().ToString("N");
        state["lastAttempt"] = 0L;
        settings.SharedOptions["subscription"] = definition.ToJson();
        settings.WindowsOptions[StateKey] = state;
    }

    public static void AdoptImport(Settings result, Settings previous, JsonObject document)
    {
        if (!document.ContainsKey("subscription"))
        {
            if (previous.WindowsOptions[StateKey] is { } state) result.WindowsOptions[StateKey] = state.DeepClone();
            return;
        }
        if (document["subscription"] is null) { Configure(result, null); return; }
        var definition = Get(result)!;
        var old = Get(previous);
        var owned = old is not null && definition.SameAccount(old) ? OwnedIds(previous) : [];
        foreach (var profile in document["profiles"]!.AsArray()) owned.Add(profile!["id"]!.GetValue<string>());
        owned.IntersectWith(result.Profiles.Select(profile => profile.Id));
        result.WindowsOptions[StateKey] = new JsonObject
        {
            ["generation"] = Guid.NewGuid().ToString("N"),
            ["ownedIds"] = new JsonArray(owned.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray())
        };
    }

    public static Settings ApplySnapshot(Settings current, ProfileImport import, int sourceIndex, DateTimeOffset now)
    {
        var subscription = Get(current) ?? throw new InvalidDataException("No subscription configured.");
        var owned = OwnedIds(current);
        ProfileTransfer.MatchListProfiles(import, current.Profiles.Where(profile => owned.Contains(profile.Id)).ToList(), subscription: true);
        var importedIds = import.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        if (current.Profiles.Any(profile => importedIds.Contains(profile.Id) && !owned.Contains(profile.Id)))
            throw new InvalidDataException("The subscription conflicts with a local profile ID.");
        if (import.Document["activeProfileId"] is { } active && !importedIds.Contains(active.GetValue<string>()))
            throw new InvalidDataException("The subscription refers to a missing active profile.");
        if (import.Document["failover"]?["profileIds"] is JsonArray backupIds && backupIds.Any(id => id is not JsonValue value || !value.TryGetValue<string>(out var text) || !importedIds.Contains(text)))
            throw new InvalidDataException("The subscription refers to a missing failover profile.");
        var document = (JsonObject)import.Document.DeepClone();
        SubscriptionDefinition next = subscription;
        if (document["subscription"] is not null && document["subscription"] is not JsonObject) throw new InvalidDataException("Invalid subscription object.");
        if (document["subscription"] is JsonObject incoming)
        {
            var downloaded = SubscriptionDefinition.Parse(incoming);
            next = subscription with { Url = downloaded.Url, FallbackUrls = downloaded.FallbackUrls };
        }
        document.Remove("subscription");
        string? selection = current.ActiveProfileId;
        var result = PortableConfiguration.Import(document.ToJsonString(), current);
        result.Profiles.RemoveAll(profile => owned.Contains(profile.Id) && !importedIds.Contains(profile.Id));
        PruneFailover(result);
        result.ActiveProfileId = result.Profiles.Any(profile => profile.Id == selection) ? selection
            : import.Document["activeProfileId"]?.GetValue<string>() ?? import.Profiles[0].Id;
        result.SharedOptions["subscription"] = next.ToJson();
        var state = (JsonObject)State(current).DeepClone();
        state["ownedIds"] = new JsonArray(importedIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        state["lastAttempt"] = now.ToUnixTimeSeconds();
        state["lastSuccess"] = now.ToUnixTimeSeconds();
        state["status"] = import.Skipped > 0 ? "Updated with warnings" : "Updated";
        state["sourceIndex"] = sourceIndex;
        result.WindowsOptions[StateKey] = state;
        PortableConfiguration.ValidateSettings(result);
        return result;
    }

    public static Settings Failed(Settings current, DateTimeOffset now)
    {
        var result = current.Copy();
        var state = (JsonObject)State(current).DeepClone();
        state["lastAttempt"] = now.ToUnixTimeSeconds(); state["status"] = "Update failed";
        result.WindowsOptions[StateKey] = state;
        return result;
    }

    public static void PruneFailover(Settings settings)
    {
        if (settings.SharedOptions["failover"]?["profileIds"] is not JsonArray ids) return;
        for (int index = ids.Count - 1; index >= 0; index--)
            if (!settings.Profiles.Any(profile => profile.Id == ids[index]!.GetValue<string>())) ids.RemoveAt(index);
    }
}
