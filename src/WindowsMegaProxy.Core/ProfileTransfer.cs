using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed record ProfileImport(JsonObject Document, bool Canonical, int Skipped)
{
    public List<ProxyProfile> Profiles { get; } = Document["profiles"]!.AsArray().Select(node => new ProxyProfile(node!.AsObject())).ToList();
}

public static class ProfileTransfer
{
    public static ProfileImport Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Configuration exceeds 4 MiB.");
        text = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            PortableConfiguration.CheckDuplicateKeys(json.RootElement);
            var root = JsonNode.Parse(text)!.AsObject();
            if (root["schema"]?.ToString() is "net.megaproxy487.config" or "dev.megaproxy.config")
            {
                int version = root["version"]?.GetValue<int>() ?? 0;
                if (version is < 1 or > 8) throw new InvalidDataException("MegaProxy JSON versions 1–8 are supported.");
                var profiles = root["profiles"]?.AsArray() ?? throw new InvalidDataException("Profile list not found.");
                if (profiles.Count is < 1 or > 1000) throw new InvalidDataException("Between 1 and 1,000 profiles are allowed.");
                for (int index = 0; index < profiles.Count; index++)
                {
                    var profile = profiles[index] as JsonObject ?? throw new InvalidDataException("Invalid profile object.");
                    if (version < 7 && string.IsNullOrWhiteSpace(profile["id"]?.ToString())) profile["id"] = "import-" + index;
                    if (profile["proxy"] is not JsonObject proxy) throw new InvalidDataException("The profile has no proxy object.");
                    if (!proxy.ContainsKey("type") || proxy["type"]?.ToString() == "") proxy["type"] = "HTTPS";
                    if (proxy["type"] is null) throw new InvalidDataException("Invalid proxy type.");
                    proxy["port"] ??= proxy["type"]!.ToString() == "SOCKS5" ? 1080 : proxy["type"]!.ToString() is "SSH" or "SSH_JUMP" ? 22 : 443;
                    if (version < 8)
                    {
                        if (version is >= 2 and <= 5 && root["routing"]?["allowIpv6"] is { } ipv6)
                        {
                            profile["routing"] ??= new JsonObject();
                            profile["routing"]!["allowIpv6"] = ipv6.DeepClone();
                        }
                    }
                    if (profile["countryCode"] is JsonValue countryValue && countryValue.TryGetValue<string>(out var country)) profile["countryCode"] = country.ToUpperInvariant();
                    if (!proxy.ContainsKey("knockHost") && profile["browser"]?["knockHost"] is { } knock) proxy["knockHost"] = knock.DeepClone();
                    PortableConfiguration.ValidateProfile(new ProxyProfile(profile), portable: true);
                }
                root["version"] = 8;
                return new(root, true, 0);
            }
            var data = root["data"]?.AsArray() ?? throw new InvalidDataException("Unsupported JSON configuration format.");
            if (data.Count > 1000) throw new InvalidDataException("At most 1,000 profiles are allowed.");
            int skipped = 0;
            var imported = new List<ProxyProfile>();
            foreach (var item in data.OfType<JsonObject>())
            {
                string type = item["type"]?.ToString().Trim().ToLowerInvariant() ?? "";
                if (type is not ("https" or "ssl" or "socks5")) { skipped++; continue; }
                string host = item["hostname"]?.ToString().Trim() ?? "";
                if (host.Length == 0) host = item["address"]?.ToString().Trim() ?? "";
                int port = int.TryParse(item["port"]?.ToString(), out int value) && value is > 0 and <= 65535 ? value : type == "socks5" ? 1080 : 443;
                imported.Add(Create(type == "socks5" ? "SOCKS5" : "HTTPS", host, port, item["username"]?.ToString() ?? "", item["password"]?.ToString(), item["title"]?.ToString() ?? "", item["cc"]?.ToString() ?? ""));
            }
            return ListDocument(imported, skipped);
        }
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        if (lines.Length is < 1 or > 1000) throw new InvalidDataException("Between 1 and 1,000 proxy entries are allowed.");
        var proxies = new List<ProxyProfile>();
        int ignored = 0;
        foreach (string line in lines)
        {
            if (line.Length > 65536 || !Uri.TryCreate(line, UriKind.Absolute, out var uri)) throw new InvalidDataException("Invalid proxy URI.");
            string type = uri.Scheme.ToLowerInvariant() switch { "https" => "HTTPS", "masque" => "MASQUE", "socks5" => "SOCKS5", _ => "" };
            if (type.Length == 0) { ignored++; continue; }
            string userInfo = uri.UserInfo;
            if (userInfo.Length == 0 && type != "SOCKS5") throw new InvalidDataException("HTTPS/MASQUE proxy URIs must include username:password@ (empty values are allowed).");
            int separator = userInfo.IndexOf(':');
            if (userInfo.Length > 0 && separator < 0) throw new InvalidDataException("Proxy credentials must use username:password.");
            string username = separator < 0 ? "" : Decode(userInfo[..separator], false);
            string? password = separator < 0 ? null : Decode(userInfo[(separator + 1)..], false);
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length > 100) throw new InvalidDataException("Too many proxy URI parameters.");
            foreach (string pair in pairs)
            {
                int split = pair.IndexOf('=');
                query[Decode(split < 0 ? pair : pair[..split], true)] = Decode(split < 0 ? "" : pair[(split + 1)..], true);
            }
            int port = uri.IsDefaultPort || uri.Port == -1 ? type == "SOCKS5" ? 1080 : 443 : uri.Port;
            proxies.Add(Create(type, uri.IdnHost.Trim('[', ']'), port, username, password, query.GetValueOrDefault("title", ""), query.GetValueOrDefault("cc", "")));
        }
        return ListDocument(proxies, ignored);
    }

    private static string Decode(string text, bool query)
    {
        for (int index = 0; index < text.Length; index++)
            if (text[index] == '%' && (index + 2 >= text.Length || !Uri.IsHexDigit(text[index + 1]) || !Uri.IsHexDigit(text[index + 2])))
                throw new InvalidDataException("Invalid URI percent encoding.");
        return Uri.UnescapeDataString(query ? text.Replace('+', ' ') : text);
    }

    private static ProxyProfile Create(string type, string host, int port, string username, string? password, string name, string country)
    {
        var profile = ProxyProfile.Create(name.Trim());
        profile.Proxy["type"] = type; profile.Proxy["host"] = host; profile.Proxy["port"] = port; profile.Proxy["username"] = username;
        if (password is not null) profile.Proxy["password"] = password;
        country = country.Trim().ToUpperInvariant();
        if (country.Length == 2 && country.All(char.IsAsciiLetterUpper)) profile.Data["countryCode"] = country;
        PortableConfiguration.ValidateProfile(profile, portable: true);
        return profile;
    }

    private static ProfileImport ListDocument(List<ProxyProfile> profiles, int skipped)
    {
        if (profiles.Count == 0) throw new InvalidDataException("No supported proxy profiles found.");
        return new(new JsonObject { ["schema"] = "net.megaproxy487.config", ["version"] = 8,
            ["profiles"] = new JsonArray(profiles.Select(profile => (JsonNode)profile.Data).ToArray()) }, false, skipped);
    }

    public static string ExportProxyList(Settings settings, bool passwords)
    {
        PortableConfiguration.ValidateSettings(settings);
        var lines = settings.Profiles.Where(profile => profile.Type is "HTTPS" or "MASQUE" or "SOCKS5").Select(profile =>
        {
            string Encode(string value) => Uri.EscapeDataString(value);
            string password = passwords ? profile.Proxy["password"]?.ToString() ?? "" : "";
            string username = profile.Proxy["username"]?.ToString() ?? "";
            string auth = profile.Type != "SOCKS5" || username.Length + password.Length > 0 ? Encode(username) + ":" + Encode(password) + "@" : "";
            string host = profile.Host.Contains(':') ? "[" + profile.Host.Trim('[', ']') + "]" : profile.Host;
            string port = profile.Port == (profile.Type == "SOCKS5" ? 1080 : 443) ? "" : ":" + profile.Port.ToString(CultureInfo.InvariantCulture);
            return profile.Type.ToLowerInvariant() + "://" + auth + host + port + "?title=" + Encode(profile.Name) + "&cc=" + Encode(profile.CountryCode);
        }).ToArray();
        if (lines.Length == 0) throw new InvalidDataException("ProxyList supports HTTPS, MASQUE and SOCKS5 profiles only.");
        string result = string.Join('\n', lines) + "\n";
        if (Encoding.UTF8.GetByteCount(result) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Export exceeds 4 MiB.");
        return result;
    }

    public static void MatchListProfiles(ProfileImport import, IReadOnlyList<ProxyProfile> previous, bool subscription)
    {
        if (import.Canonical) return;
        var sources = import.Profiles;
        var used = new HashSet<string>();
        string Endpoint(ProxyProfile profile) => profile.Type + "\0" + profile.Host.TrimEnd('.').ToLowerInvariant() + "\0" + profile.Port;
        foreach (var profile in sources)
        {
            string name = profile.Data["name"]?.ToString() ?? "";
            var names = previous.Where(old => name.Length > 0 && old.Name == name).ToArray();
            var endpoints = previous.Where(old => Endpoint(old) == Endpoint(profile) && (subscription || old.Proxy["username"]?.ToString() == profile.Proxy["username"]?.ToString())).ToArray();
            ProxyProfile? match = subscription && names.Length == 1 && sources.Count(item => item.Data["name"]?.ToString() == name) == 1 ? names[0]
                : endpoints.Length == 1 && sources.Count(item => Endpoint(item) == Endpoint(profile)) == 1 ? endpoints[0] : null;
            if (match is null || !used.Add(match.Id)) continue;
            profile.Data["id"] = match.Id;
            if (name.Length == 0) profile.Data["name"] = match.Name;
            if (profile.CountryCode.Length == 0) profile.Data["countryCode"] = match.CountryCode;
            if (profile.Proxy["password"]?.ToString() == "") profile.Proxy.Remove("password");
        }
    }
}
