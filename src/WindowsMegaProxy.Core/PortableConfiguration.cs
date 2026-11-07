using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WindowsMegaProxy;

public static class PortableConfiguration
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions PortableOptions = new(Configuration.JsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Settings Copy(Settings settings) => settings.Copy();

    public static string ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw Invalid(Ui.T("File size exceeds 4 MiB."));
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var text = reader.ReadToEnd();
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw Invalid(Ui.T("File size exceeds 4 MiB."));
        return text;
    }

    public static Settings Import(string text, Settings current)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw Invalid(Ui.T("File size exceeds 4 MiB."));
            using var document = JsonDocument.Parse(text);
            CheckDuplicateKeys(document.RootElement);
            var root = JsonNode.Parse(text)?.AsObject() ?? throw Invalid(Ui.T("Expected a JSON object."));
            if (String(root, "schema") is not ("net.megaproxy487.config" or "dev.megaproxy.config") || root["version"]?.GetValue<int>() != 8)
                throw Invalid(Ui.T("MegaProxy JSON version 8 is supported."));
            var imported = root["profiles"]?.AsArray() ?? throw Invalid(Ui.T("Profile list not found."));
            if (imported.Count is < 1 or > 1000) throw Invalid(Ui.T("Between 1 and 1,000 profiles are allowed."));
            var candidate = Copy(current);
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var node in imported)
            {
                var profile = new ProxyProfile(node?.AsObject() ?? throw Invalid(Ui.T("Invalid profile.")));
                ValidateProfile(profile, portable: true);
                if (!ids.Add(profile.Id)) throw Invalid(Ui.T("The file contains duplicate profile IDs."));
                int index = candidate.Profiles.FindIndex(existing => existing.Id == profile.Id);
                if (index >= 0)
                {
                    var existing = candidate.Profiles[index];
                    var merged = Merge(existing.Data, profile.Data);
                    bool sameAccount = MergeProxySecrets(merged["proxy"]!.AsObject(), profile.Proxy, existing.Proxy);
                    if (!sameAccount && merged["browser"] is JsonObject browser && (profile.Data["browser"] as JsonObject)?.ContainsKey("knockHost") != true) browser.Remove("knockHost");
                    candidate.Profiles[index] = new ProxyProfile(merged);
                }
                else candidate.Profiles.Add(profile.Copy());
            }
            var shared = (JsonObject)root.DeepClone();
            shared.Remove("profiles"); shared.Remove("windows"); shared.Remove("activeProfileId");
            if (shared["subscription"] is JsonObject incomingSubscription)
            {
                var definition = SubscriptionDefinition.Parse(incomingSubscription);
                var previous = ConfigurationSubscription.Get(candidate);
                if (definition.Password is null && previous?.Password is not null && definition.SameAccount(previous)) incomingSubscription["password"] = previous.Password;
            }
            candidate.SharedOptions = Merge(candidate.SharedOptions, shared);
            if (shared.ContainsKey("subscription")) candidate.SharedOptions["subscription"] = shared["subscription"]?.DeepClone();
            // Subscription credentials belong to their URL/account, never to a replacement endpoint.
            if (shared["subscription"] is JsonObject subscription && !subscription.ContainsKey("password"))
                candidate.SharedOptions["subscription"]!.AsObject().Remove("password");
            if (shared["subscription"] is JsonObject account && !account.ContainsKey("username"))
                candidate.SharedOptions["subscription"]!.AsObject().Remove("username");
            if (root.ContainsKey("windows") && root["windows"] is not JsonObject) throw Invalid(Ui.T("Windows settings must be an object."));
            if (root["windows"] is JsonObject windows)
            {
                windows.Remove(ConfigurationSubscription.StateKey);
                windows.Remove(PodkopCatalog.WarningKey);
                candidate.WindowsOptions = Merge(candidate.WindowsOptions, windows);
                if (!windows.ContainsKey("version") || windows["version"]?.GetValue<int>() == 1)
                {
                    if (windows.ContainsKey("defaultAction")) candidate.DefaultAction = String(windows, "defaultAction");
                    if (windows.ContainsKey("bypassLocalNetworks")) candidate.BypassLan = windows["bypassLocalNetworks"]!.GetValue<bool>();
                    if (windows.ContainsKey("autoConnect")) candidate.AutoConnect = windows["autoConnect"]!.GetValue<bool>();
                    if (windows.ContainsKey("logSizeMb")) candidate.LogSizeMb = windows["logSizeMb"]!.GetValue<int>();
                    if (windows.ContainsKey("showAllConnections")) candidate.ShowAllConnections = windows["showAllConnections"]!.GetValue<bool>();
                    if (windows.ContainsKey("language")) candidate.Language = String(windows, "language");
                    if (windows.ContainsKey("theme")) candidate.Theme = String(windows, "theme");
                    if (windows.ContainsKey("tlsFingerprint")) candidate.TlsFingerprint = String(windows, "tlsFingerprint");
                    if (windows.ContainsKey("launchAtSignIn")) candidate.LaunchAtSignIn = windows["launchAtSignIn"]!.GetValue<bool>();
                    if (windows.ContainsKey("rules")) candidate.Rules = windows["rules"]!.Deserialize<List<RoutingRule>>(PortableOptions) ?? throw Invalid(Ui.T("Invalid Windows rules."));
                }
            }
            var platform = root["windows"] as JsonObject;
            if (platform?.ContainsKey("tlsFingerprint") != true && root["tls"]?["fingerprint"] is { } fingerprint)
            {
                string value = fingerprint.GetValue<string>().ToLowerInvariant();
                if (Configuration.TlsFingerprints.Contains(value)) candidate.TlsFingerprint = value;
            }
            if (platform?.ContainsKey("logSizeMb") != true && root["diagnosticLogLimitMb"] is { } logSize) candidate.LogSizeMb = Math.Clamp(logSize.GetValue<int>(), 1, 100);
            if (platform?.ContainsKey("bypassLocalNetworks") != true && root["routing"]?["bypassLocalNetworks"] is { } bypass) candidate.BypassLan = bypass.GetValue<bool>();
            var requestedId = root["activeProfileId"]?.GetValue<string>();
            if (requestedId is not null && !candidate.Profiles.Any(profile => profile.Id == requestedId)) throw Invalid(Ui.T("The active profile is missing from the configuration."));
            candidate.ActiveProfileId = requestedId ?? candidate.ActiveProfileId;
            if (candidate.ActiveProfile is null) candidate.ActiveProfileId = candidate.Profiles[0].Id;
            ValidateSettings(candidate);
            return candidate;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw Invalid(Ui.T("Invalid MegaProxy.json format. Settings were not changed."));
        }
    }

    public static JsonObject Export(Settings settings, bool includePasswords = false, bool includePrivateKeys = false)
    {
        ValidateSettings(settings);
        if (settings.Profiles.Count == 0) throw Invalid(Ui.T("Add at least one profile before exporting."));
        foreach (var profile in settings.Profiles) ValidateProfile(profile, portable: true);
        var result = (JsonObject)settings.SharedOptions.DeepClone();
        result["schema"] = "net.megaproxy487.config";
        result["version"] = 8;
        result["activeProfileId"] = settings.ActiveProfileId;
        result["profiles"] = new JsonArray(settings.Profiles.Select(profile => (JsonNode)profile.Data.DeepClone()).ToArray());
        foreach (var node in result["profiles"]!.AsArray())
        {
            var profile = new ProxyProfile(node!.AsObject());
            if (profile.Type == "HTTPS" && !profile.Proxy.ContainsKey("knockHost") && ProxyAuthenticationProbe.Host(profile).Length > 0)
                profile.Proxy["knockHost"] = ProxyAuthenticationProbe.Host(profile);
        }
        var windows = (JsonObject)settings.WindowsOptions.DeepClone();
        windows.Remove(ConfigurationSubscription.StateKey);
        windows.Remove(PodkopCatalog.WarningKey);
        if (!windows.ContainsKey("version") || windows["version"]?.GetValue<int>() == 1)
        {
            windows["version"] = 1;
            windows["defaultAction"] = settings.DefaultAction;
            windows["bypassLocalNetworks"] = settings.BypassLan;
            windows["autoConnect"] = settings.AutoConnect;
            windows["logSizeMb"] = settings.LogSizeMb;
            windows["showAllConnections"] = settings.ShowAllConnections;
            windows["language"] = settings.Language;
            windows["theme"] = settings.Theme;
            windows["tlsFingerprint"] = settings.TlsFingerprint;
            windows["launchAtSignIn"] = settings.LaunchAtSignIn ?? false;
            windows["rules"] = JsonSerializer.SerializeToNode(settings.Rules, PortableOptions);
        }
        result["windows"] = windows;
        RemoveSecrets(result, includePasswords, includePrivateKeys);
        result["passwordsIncluded"] = includePasswords;
        result["privateKeysIncluded"] = includePrivateKeys;
        if (Encoding.UTF8.GetByteCount(result.ToJsonString(Configuration.JsonOptions)) > MaximumBytes)
            throw Invalid(Ui.T("Export exceeds the 4 MiB limit."));
        return result;
    }

    public static void ValidateSettings(Settings settings)
    {
        if (settings.Profiles is null || settings.Rules is null || settings.SharedOptions is null || settings.WindowsOptions is null)
            throw Invalid(Ui.T("Invalid local settings."));
        if (settings.Profiles.Count > 1000 || settings.Rules.Count > 1000) throw Invalid(Ui.T("At most 1,000 profiles and rules are allowed."));
        if (settings.LogSizeMb is < 1 or > 100) throw Invalid(Ui.T("Log size must be between 1 and 100 MiB."));
        if (settings.Language is not ("en" or "ru") || settings.Theme is not ("auto" or "light" or "dark")) throw Invalid("Invalid language or theme.");
        if (!Configuration.TlsFingerprints.Contains(settings.TlsFingerprint)) throw Invalid("Unsupported TLS fingerprint.");
        _ = ConfigurationSubscription.Get(settings);
        FailoverPolicy.Validate(settings);
        var ids = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var profile in settings.Profiles)
        {
            ValidateProfile(profile);
            if (!ids.Add(profile.Id)) throw Invalid(Ui.T("Duplicate profile IDs."));
        }
        if (settings.ActiveProfileId is not null && !ids.Contains(settings.ActiveProfileId)) throw Invalid(Ui.T("The selected profile is missing."));
        if (settings.DefaultAction == "direct") settings.DefaultAction = "system";
        if (settings.DefaultAction is not ("proxy" or "system" or "block")) throw Invalid(Ui.T("Invalid default action."));
        foreach (var rule in settings.Rules)
        {
            if (rule is null) throw Invalid(Ui.T("Empty Windows rule."));
            if (rule.Action == "direct") rule.Action = "system";
            rule.ProfileId = null;
            rule.ProfileName = null;
            Configuration.BuildRule(rule);
        }
    }

    public static void ValidateProfile(ProxyProfile profile, bool portable = false)
    {
        var data = profile.Data;
        var id = String(data, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || String(data, "name").Length > 256) throw Invalid(Ui.T("Invalid profile name or ID."));
        if (data["color"] is not null && data["color"]!.GetValue<int>() < 0) throw Invalid(Ui.T("Invalid profile color."));
        var country = String(data, "countryCode");
        if (country.Length != 0 && (country.Length != 2 || country.Any(character => character is < 'A' or > 'Z'))) throw Invalid(Ui.T("Invalid country code."));
        var proxy = data["proxy"]?.AsObject() ?? throw Invalid(Ui.T("The profile has no proxy object."));
        if (data["windows"] is JsonObject options && options.ContainsKey("sshHostKey") &&
            (options["sshHostKey"] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var rawKey) || rawKey.Length > 16384))
            throw Invalid(Ui.T("Enter an OpenSSH server public key."));
        var type = String(proxy, "type");
        if (type is not ("HTTPS" or "SOCKS5" or "HTTPS_JUMP" or "SSH" or "SSH_JUMP" or "MASQUE") && !(type == "HTTP" && !portable))
            throw Invalid(Ui.T("Profile type is not part of the MegaProxy v8 format."));
        ValidateEndpoint(proxy);
        if (proxy.ContainsKey("preferHttp3") && (proxy["preferHttp3"] is not JsonValue preference || !preference.TryGetValue<bool>(out _)))
            throw Invalid("preferHttp3 must be a boolean.");
        if (proxy.ContainsKey("authProbeHost")) ProxyAuthenticationProbe.ValidateHost(String(proxy, "authProbeHost"));
        if (proxy.ContainsKey("knockHost") && proxy["knockHost"] is null) throw Invalid("Use an empty knockHost to clear it.");
        ProxyAuthenticationProbe.ValidateHost(ProxyAuthenticationProbe.Host(profile));
        if (type is "HTTPS_JUMP" or "SSH_JUMP") ValidateEndpoint(proxy["jump"]?.AsObject() ?? throw Invalid(Ui.T("The jump server is not configured.")));
        CheckSecrets(proxy);
        if (type != "SOCKS5" && String(proxy, "username").Contains(':')) throw Invalid(Ui.T("The username must not contain a colon."));
        if (proxy["jump"] is JsonObject jump)
        {
            CheckSecrets(jump);
            if (type == "HTTPS_JUMP" && String(jump, "username").Contains(':')) throw Invalid(Ui.T("The username must not contain a colon."));
        }
    }

    private static void ValidateEndpoint(JsonObject endpoint)
    {
        var host = String(endpoint, "host");
        if (host.Length is < 1 or > 253 || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown || host.Any(char.IsWhiteSpace))
            throw Invalid(Ui.T("Enter a valid server name or IP without a scheme or port."));
        if (endpoint["port"]?.GetValue<int>() is not (>= 1 and <= 65535)) throw Invalid(Ui.T("Port must be between 1 and 65535."));
    }

    private static void CheckSecrets(JsonObject endpoint)
    {
        if (String(endpoint, "trustedHostKey").Length > 256 || String(endpoint, "sshProfile").Length > 256)
            throw Invalid(Ui.T("Unsupported SSH fingerprint."));
        foreach (var flag in new[] { "allowInvalidProxyCertificate", "acceptAnyHostKey", "sameAuthentication" })
            if (endpoint.ContainsKey(flag)) _ = endpoint[flag]?.GetValue<bool>() ?? throw Invalid(Ui.T("Invalid profile trust setting."));
        foreach (var key in new[] { "username", "password", "privateKey" })
            if (endpoint.ContainsKey(key) && endpoint[key] is null) throw Invalid(Ui.T("Use an empty value to clear credentials."));
        if (String(endpoint, "username").Length > 4096 || String(endpoint, "username").IndexOfAny(['\r', '\n']) >= 0)
            throw Invalid(Ui.T("Invalid proxy username."));
        if (String(endpoint, "password").Length > 16384 || String(endpoint, "password").IndexOfAny(['\r', '\n']) >= 0 || String(endpoint, "privateKey").Length > 65536)
            throw Invalid(Ui.T("Invalid profile credentials."));
    }

    private static JsonObject Merge(JsonObject existing, JsonObject incoming)
    {
        var result = (JsonObject)existing.DeepClone();
        foreach (var field in incoming)
            result[field.Key] = field.Value is JsonObject child && result[field.Key] is JsonObject previous ? Merge(previous, child) : field.Value?.DeepClone();
        return result;
    }

    private static bool SameEndpoint(JsonObject first, JsonObject second) =>
        System.String.Equals(String(first, "host").Trim('[', ']').TrimEnd('.'), String(second, "host").Trim('[', ']').TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && first["port"]?.GetValue<int>() == second["port"]?.GetValue<int>() && String(first, "username") == String(second, "username");

    private static bool MergeProxySecrets(JsonObject result, JsonObject incoming, JsonObject previous)
    {
        bool sameProtocol = String(incoming, "type") == String(previous, "type");
        bool sharedJump = String(incoming, "type") is "HTTPS_JUMP" or "SSH_JUMP" && (incoming["jump"]?["sameAuthentication"]?.GetValue<bool>() ?? true);
        bool sameMain = sameProtocol && SameEndpoint(incoming, previous);
        if (sharedJump) sameMain &= incoming["jump"] is JsonObject jump && previous["jump"] is JsonObject oldJump && SameEndpoint(jump, oldJump);
        MergeEndpointSecrets(result, incoming, previous, sameMain);
        if (!incoming.ContainsKey("username")) result.Remove("username");
        if (result["jump"] is JsonObject resultJump)
        {
            var newJump = incoming["jump"] as JsonObject ?? new JsonObject();
            var oldJump = previous["jump"] as JsonObject ?? new JsonObject();
            bool sameJump = sameProtocol && !sharedJump && !(oldJump["sameAuthentication"]?.GetValue<bool>() ?? true) && SameEndpoint(newJump, oldJump);
            MergeEndpointSecrets(resultJump, newJump, oldJump, sameJump);
            if (!newJump.ContainsKey("username")) resultJump.Remove("username");
        }
        return sameMain;
    }

    private static void MergeEndpointSecrets(JsonObject result, JsonObject incoming, JsonObject previous, bool keepOmitted)
    {
        foreach (var key in new[] { "password", "privateKey" })
        {
            result.Remove(key);
            if (incoming.ContainsKey(key)) result[key] = incoming[key]?.DeepClone();
            else if (keepOmitted && previous.ContainsKey(key)) result[key] = previous[key]?.DeepClone();
        }
        foreach (var key in new[] { "allowInvalidProxyCertificate", "acceptAnyHostKey", "trustedHostKey", "knockHost", "authProbeHost" })
            if (!keepOmitted && !incoming.ContainsKey(key)) result.Remove(key);
    }

    private static void RemoveSecrets(JsonNode node, bool passwords, bool keys)
    {
        if (node is JsonObject obj)
        {
            foreach (var field in obj.ToArray())
            {
                if ((!passwords && field.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) || (!keys && field.Key.Equals("privateKey", StringComparison.OrdinalIgnoreCase))) obj.Remove(field.Key);
                else if (field.Value is not null) RemoveSecrets(field.Value, passwords, keys);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) RemoveSecrets(child, passwords, keys);
    }

    internal static void CheckDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw Invalid(Ui.T("Duplicate JSON fields are not allowed."));
                CheckDuplicateKeys(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckDuplicateKeys(child);
    }

    private static string String(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? "";
    private static InvalidDataException Invalid(string message) => new(message);
}
