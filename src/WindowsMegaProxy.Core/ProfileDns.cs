using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class ProfileDns
{
    public static readonly (string Id, string Name, string Url)[] Providers =
    [
        ("CLOUDFLARE", "Cloudflare", "https://cloudflare-dns.com/dns-query"),
        ("GOOGLE", "Google", "https://dns.google/dns-query"),
        ("QUAD9", "Quad9", "https://dns.quad9.net/dns-query"),
        ("YANDEX", "Yandex Basic", "https://common.dot.dns.yandex.net/dns-query"),
        ("YANDEX_SAFE", "Yandex Safe", "https://safe.dot.dns.yandex.net/dns-query"),
        ("YANDEX_FAMILY", "Yandex Family", "https://family.dot.dns.yandex.net/dns-query"),
        ("CUSTOM", "Custom DNS URL", "")
    ];

    public static string Provider(ProxyProfile profile) => profile.Data["dns"]?["provider"]?.GetValue<string>() ?? "CLOUDFLARE";
    public static string CustomUrl(ProxyProfile profile) => profile.Data["dns"]?["customDohUrl"]?.GetValue<string>() ?? "";

    public static JsonObject BuildBootstrap(string provider, bool proxyDefault)
    {
        if (provider == "auto") provider = "CLOUDFLARE";
        if (provider == "system") return new JsonObject { ["type"] = "local", ["tag"] = "bootstrap" };
        string address = provider switch
        {
            "CLOUDFLARE" => "1.1.1.1", "GOOGLE" => "8.8.8.8", "QUAD9" => "9.9.9.9",
            "YANDEX" => "77.88.8.8", "YANDEX_SAFE" => "77.88.8.88", "YANDEX_FAMILY" => "77.88.8.7",
            _ => throw new ArgumentException(Ui.T("Invalid bootstrap DNS provider."))
        };
        var endpoint = new Uri(Providers.First(item => item.Id == provider).Url);
        return new JsonObject
        {
            ["type"] = "https", ["tag"] = "bootstrap", ["server"] = address, ["path"] = endpoint.AbsolutePath,
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = endpoint.Host }
        };
    }

    public static JsonObject Build(ProxyProfile profile)
    {
        string provider = Provider(profile);
        string? address = provider == "CUSTOM" ? CustomUrl(profile).Trim() : Providers.FirstOrDefault(item => item.Id == provider).Url;
        if (address is null) throw new ArgumentException(Ui.T("This DNS provider is not supported. Choose another provider in the profile."));
        if (provider == "CUSTOM" && Uri.TryCreate(address, UriKind.Absolute, out var plain) && plain.Scheme is "tcp" or "udp")
        {
            if (address.Length > 2048 || address.Any(char.IsWhiteSpace) || !IPAddress.TryParse(plain.Host.Trim('[', ']'), out _) ||
                plain.UserInfo.Length != 0 || plain.Query.Length != 0 || plain.Fragment.Length != 0 || plain.AbsolutePath is not ("" or "/") ||
                plain.Port != -1 && plain.Port is < 1 or > 65535)
                throw new ArgumentException(Ui.T("Plain DNS requires a numeric IP and an optional port."));
            if (plain.Scheme == "udp" && profile.Type != "SOCKS5" && !profile.UsesHttp3)
                throw new ArgumentException(Ui.T("UDP DNS requires SOCKS5 UDP or HTTP/3. Use TCP DNS for this profile."));
            return new JsonObject
            {
                ["type"] = plain.Scheme, ["tag"] = "dns", ["server"] = plain.Host.Trim('[', ']'),
                ["server_port"] = plain.Port == -1 ? 53 : plain.Port, ["detour"] = "proxy"
            };
        }
        if (address.Length > 2048 || address.Any(char.IsWhiteSpace) || address.Contains('#') ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Port is < 1 or > 65535)
            throw new ArgumentException(Ui.T("Enter an HTTPS DoH URL or tcp://IP:port / udp://IP:port."));
        // sing-box treats '?' in its path field as a literal path character, not a query separator.
        if (uri.Query.Length != 0) throw new ArgumentException(Ui.T("DoH URL query parameters are not supported by the bundled core. Use an endpoint without a query string."));
        return new JsonObject
        {
            ["type"] = "https", ["tag"] = "dns", ["server"] = uri.IdnHost.Trim('[', ']'),
            ["server_port"] = uri.Port, ["path"] = uri.AbsolutePath, ["detour"] = "proxy"
        };
    }
}
