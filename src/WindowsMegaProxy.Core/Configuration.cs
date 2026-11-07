using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WindowsMegaProxy;

public sealed class Settings
{
    public List<ProxyProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public JsonObject SharedOptions { get; set; } = new();
    public JsonObject WindowsOptions { get; set; } = new();
    public string DefaultAction { get; set; } = "system";
    public bool BypassLan { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool? LaunchAtSignIn { get; set; }
    public int LogSizeMb { get; set; } = 5;
    public bool ShowAllConnections { get; set; }
    public string Language { get; set; } = Ui.DefaultLanguage(Ui.WindowsLanguage);
    public string Theme { get; set; } = "auto";
    public string TlsFingerprint { get; set; } = "default";
    public List<RoutingRule> Rules { get; set; } = [];

    [JsonIgnore] public ProxyProfile? ActiveProfile => Profiles.FirstOrDefault(profile => profile.Id == ActiveProfileId);

    public Settings Copy()
    {
        var result = (Settings)MemberwiseClone();
        result.Profiles = Profiles.Select(profile => profile.Copy()).ToList();
        result.Rules = Rules.Select(rule => rule.Copy()).ToList();
        result.SharedOptions = (JsonObject)SharedOptions.DeepClone();
        result.WindowsOptions = (JsonObject)WindowsOptions.DeepClone();
        return result;
    }
}

public sealed class RoutingRule : INotifyPropertyChanged
{
    private bool enabled = true;
    public string Name { get; set; } = "";
    public bool Enabled
    {
        get => enabled;
        set { if (enabled == value) return; enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage()
    {
        foreach (string name in new[] { nameof(NameLabel), nameof(SourceLabel), nameof(DestinationLabel), nameof(ActionLabel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public string Source { get; set; } = "all";
    public string Path { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Action { get; set; } = "proxy";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProfileId { get; set; }
    [JsonIgnore] public string? ProfileName { get; set; }
    public RoutingRule Copy()
    {
        var copy = (RoutingRule)MemberwiseClone();
        copy.PropertyChanged = null;
        return copy;
    }
    [JsonIgnore] public string SourceLabel => Source == "all" ? Ui.T("All applications") : Path;
    [JsonIgnore] public string NameLabel => Name.Length > 0 ? Name : Source == "all" ? DestinationLabel : System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    [JsonIgnore] public string DestinationLabel => Destination.Length == 0 ? Ui.T("All destinations") : Destination;
    [JsonIgnore] public string ActionLabel => Action switch
    {
        "system" or "direct" => Ui.T("System"), "block" => Ui.T("Block"),
        _ => Ui.T("Active profile")
    };
    public override string ToString() => $"{SourceLabel} → {DestinationLabel} → {ActionLabel}";
}

public static class Configuration
{
    public const string CoreVersion = "1.14.4";
    public const string Http3CoreVersion = "1.15.0-alpha.13";
    public const string DefaultTlsFingerprint = "chrome";
    public static readonly string[] TlsFingerprints = ["default", "chrome", "firefox", "edge", "safari", "ios", "android", "randomized", "native"];
    public static string ResolveTlsFingerprint(string value) => value == "default" ? DefaultTlsFingerprint : value;
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static bool UsesProxy(Settings settings) => settings.DefaultAction == "proxy" || settings.Rules.Any(rule => rule.Enabled && rule.Action == "proxy");
    public static bool RequiresHttp3(Settings settings) => UsesProxy(settings) && settings.ActiveProfile?.UsesHttp3 == true;

    public static string Signature(Settings settings)
    {
        var snapshot = new JsonObject
        {
            ["configuration"] = Configuration.Build(settings, 1, "snapshot", allowUnresolvedSsh: true),
            ["tcpFingerprint"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type is "HTTPS" or "HTTPS_JUMP" ? ResolveTlsFingerprint(settings.TlsFingerprint) : null,
            ["transport"] = Configuration.UsesProxy(settings) ? settings.ActiveProfile?.Type : null,
            ["preferHttp3"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.PreferHttp3 == true,
            ["sshFingerprint"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type == "SSH" ? settings.ActiveProfile.Proxy["trustedHostKey"]?.DeepClone() : null,
            ["acceptAnySshKey"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type == "SSH" && (settings.ActiveProfile.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false)
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.ToJsonString())));
    }

    public static string FolderPattern(string folder)
    {
        if (!System.IO.Path.IsPathFullyQualified(folder))
            throw new ArgumentException(Ui.T("Select an absolute folder path."));
        return @"(?i)^" + Regex.Escape(System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\");
    }

    public static JsonObject Build(Settings settings, int apiPort, string apiSecret, bool allowUnresolvedSsh = false)
    {
        ValidateAction(settings.DefaultAction);
        bool proxyDefault = settings.DefaultAction == "proxy";
        var outbounds = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        if (UsesProxy(settings))
        {
            var selected = settings.ActiveProfile ?? throw new ArgumentException(Ui.T("Select a connection profile."));
            outbounds.Insert(0, BuildOutbound(selected, "proxy", settings.TlsFingerprint, allowUnresolvedSsh));
            if (selected.Type == "HTTPS_JUMP") outbounds.Insert(1, BuildOutbound(JumpProfile(selected), "jump", settings.TlsFingerprint));
        }

        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            proxyDefault ? new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
                : new JsonObject { ["protocol"] = "dns", ["action"] = "route", ["outbound"] = "direct" },
            new JsonObject { ["process_name"] = new JsonArray("sing-box.exe", "sing-box-dev.exe", "WindowsMegaProxy.exe", "WindowsMegaProxy.Service.exe", "WindowsMegaProxy.Dev.exe", "WindowsMegaProxy.Dev.Service.exe"), ["action"] = "route", ["outbound"] = "direct" }
        };
        foreach (var rule in settings.Rules.Where(rule => rule.Enabled))
            rules.Add(BuildRule(rule));
        if (settings.BypassLan)
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        if (settings.DefaultAction == "block") rules.Add(new JsonObject { ["action"] = "reject" });

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = proxyDefault ? new JsonArray(
                    new JsonObject { ["type"] = "https", ["tag"] = "bootstrap", ["server"] = "1.1.1.1", ["path"] = "/dns-query" },
                    new JsonObject { ["type"] = "https", ["tag"] = "dns", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "proxy" })
                    : new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "bootstrap" }),
                ["final"] = proxyDefault ? "dns" : "bootstrap",
                ["strategy"] = "prefer_ipv4", ["reverse_mapping"] = true
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "tun", ["interface_name"] = AppIdentity.DisplayName,
                ["address"] = new JsonArray("172.29.0.1/30", "fd87:6d65:6761::1/126"),
                ["mtu"] = 1500, ["auto_route"] = true, ["strict_route"] = proxyDefault,
                ["dns_mode"] = proxyDefault ? "hijack" : "disabled"
            }),
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true, ["find_process"] = true, ["default_domain_resolver"] = "bootstrap", ["rules"] = rules,
                ["final"] = proxyDefault ? "proxy" : "direct"
            },
            ["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{apiPort}", ["secret"] = apiSecret } }
        };
    }

    public static JsonObject BuildOutbound(ProxyProfile profile, string tag, string tlsFingerprint = "default", bool allowUnresolvedSsh = false)
    {
        PortableConfiguration.ValidateProfile(profile);
        if (!profile.Supported) throw new ArgumentException(profile.Availability);
        if (profile.Type == "SSH") return SshConfiguration.Build(profile, tag, allowUnresolvedSsh);
        if (profile.Type == "HTTPS_JUMP")
        {
            var exit = profile.Copy(); exit.Proxy["type"] = "HTTPS";
            var output = BuildOutbound(exit, tag, tlsFingerprint);
            output["detour"] = "jump";
            return output;
        }
        var proxy = profile.Proxy;
        var outbound = new JsonObject
        {
            ["type"] = profile.Type == "SOCKS5" ? "socks" : "http", ["tag"] = tag,
            ["server"] = profile.Host.Trim('[', ']'), ["server_port"] = profile.Port
        };
        if (proxy["username"] is not null || proxy["password"] is not null)
        {
            outbound["username"] = proxy["username"]?.DeepClone() ?? JsonValue.Create("");
            outbound["password"] = proxy["password"]?.DeepClone() ?? JsonValue.Create("");
        }
        if (profile.Type == "SOCKS5") outbound["version"] = "5";
        else if (outbound["username"]?.ToString() == "" && outbound["password"]?.ToString() is { Length: > 0 } password)
        {
            // sing-box only adds Basic authentication itself when the username is nonempty.
            outbound["headers"] = new JsonObject { ["Proxy-Authorization"] = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + password)) };
        }
        if (profile.Type is "HTTPS" or "MASQUE") outbound["tls"] = new JsonObject
        {
            ["enabled"] = true, ["server_name"] = profile.Host.Trim('[', ']'),
            ["insecure"] = proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false
        };
        if (profile.UsesHttp3)
        {
            outbound["version"] = 3;
            outbound["disable_version_fallback"] = true;
            outbound["tls"]!["handshake_timeout"] = "3s";
        }
        else if (profile.Type == "HTTPS")
        {
            // The stable core's CONNECT client speaks HTTP/1.1, including when uTLS offers HTTP/2 by default.
            outbound["tls"]!["alpn"] = new JsonArray("http/1.1");
            if (!TlsFingerprints.Contains(tlsFingerprint)) throw new ArgumentException("Unsupported TLS fingerprint.");
            tlsFingerprint = ResolveTlsFingerprint(tlsFingerprint);
            if (tlsFingerprint != "native")
            {
                outbound["tls"]!["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = tlsFingerprint };
            }
        }
        return outbound;
    }

    public static ProxyProfile JumpProfile(ProxyProfile profile)
    {
        var jump = profile.Proxy["jump"]!.AsObject();
        bool shared = jump["sameAuthentication"]?.GetValue<bool>() ?? true;
        var result = ProxyProfile.Create("Jump");
        result.Proxy["host"] = jump["host"]!.DeepClone(); result.Proxy["port"] = jump["port"]!.DeepClone();
        foreach (string key in new[] { "username", "password" }) result.Proxy[key] = (shared ? profile.Proxy[key] : jump[key])?.DeepClone();
        result.Proxy["allowInvalidProxyCertificate"] = jump["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false;
        result.Proxy["preferHttp3"] = profile.PreferHttp3;
        return result;
    }

    public static JsonObject BuildRule(RoutingRule rule)
    {
        if (rule.Name is null || rule.Name.Length > 80 || rule.Name.Any(char.IsControl))
            throw new ArgumentException(Ui.T("Rule names must be at most 80 characters without line breaks."));
        ValidateAction(rule.Action);
        var result = new JsonObject();
        switch (rule.Source)
        {
            case "all": break;
            case "exe":
                if (!System.IO.Path.IsPathFullyQualified(rule.Path) || !rule.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(Ui.T("Select the full EXE path."));
                result["process_path_regex"] = new JsonArray(@"(?i)^" + Regex.Escape(System.IO.Path.GetFullPath(rule.Path)) + "$");
                break;
            case "folder": result["process_path_regex"] = new JsonArray(FolderPattern(rule.Path)); break;
            default: throw new ArgumentException(Ui.T("Unknown rule source type."));
        }
        var destination = rule.Destination.Trim();
        if (destination.Length > 0)
        {
            if (IPAddress.TryParse(destination, out var ip))
                result["ip_cidr"] = new JsonArray(ip + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
            else if (destination.Contains('/'))
            {
                if (!IPNetwork.TryParse(destination, out _)) throw new ArgumentException(Ui.T("Invalid IP subnet."));
                result["ip_cidr"] = new JsonArray(destination);
            }
            else
            {
                bool suffix = destination.StartsWith("*.", StringComparison.Ordinal);
                var domain = suffix ? destination[2..] : destination;
                if (Uri.CheckHostName(domain) != UriHostNameType.Dns) throw new ArgumentException(Ui.T("Invalid domain."));
                result[suffix ? "domain_suffix" : "domain"] = new JsonArray(domain);
            }
        }
        result["action"] = rule.Action == "block" ? "reject" : "route";
        if (rule.Action != "block") result["outbound"] = rule.Action == "proxy"
            ? "proxy" : "direct";
        return result;
    }

    private static void ValidateAction(string action)
    {
        if (action is not ("system" or "direct" or "proxy" or "block")) throw new ArgumentException(Ui.T("Unknown rule action."));
    }
}
