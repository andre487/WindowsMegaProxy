using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class ConfigurationFields
{
    public const string UnknownWarning = "Configuration contains unsupported fields. They will not be imported.";
    public const string FingerprintWarning = "Unsupported fingerprints will be reset to Default.";

    public static string NormalizeFingerprint(string value) => value.ToUpperInvariant() switch
    {
        "CHROME" or "ANDROID" => "CHROME_ANDROID",
        "FIREFOX" => "FIREFOX_ANDROID",
        "EDGE" => "EDGE_ANDROID",
        "DEFAULT" or "CHROME_ANDROID" or "FIREFOX_ANDROID" or "EDGE_ANDROID" or "RANDOMIZED" => value.ToUpperInvariant(),
        _ => "DEFAULT"
    };

    public static JsonObject Project(JsonObject source, ICollection<string>? warnings = null)
    {
        var root = (JsonObject)source.DeepClone();
        void Keep(JsonObject obj, string keys)
        {
            var allowed = keys.Split(' ').ToHashSet(StringComparer.Ordinal);
            foreach (string key in obj.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).ToArray())
            {
                obj.Remove(key);
                if (warnings is not null && !warnings.Contains(UnknownWarning)) warnings.Add(UnknownWarning);
            }
        }
        void Fingerprint(JsonObject obj, string key, bool ssh = false)
        {
            if (obj[key] is null) return;
            string value = obj[key]!.GetValue<string>();
            string normalized = ssh ? SshConfiguration.Profiles.Contains(value) ? value : "DEFAULT" : NormalizeFingerprint(value);
            if (normalized == "DEFAULT" && !value.Equals("default", StringComparison.OrdinalIgnoreCase) && warnings is not null && !warnings.Contains(FingerprintWarning))
                warnings.Add(FingerprintWarning);
            obj[key] = normalized;
        }
        void Tls(JsonObject obj)
        {
            if (obj["tls"] is not JsonObject tls) return;
            Fingerprint(tls, "fingerprint");
            Keep(tls, "fingerprint");
        }
        void Proxy(JsonObject proxy, bool jump = false)
        {
            Keep(proxy, jump ? "host port sameAuthentication username password privateKey trustedHostKey acceptAnyHostKey allowInvalidProxyCertificate"
                : "type host port username password privateKey allowInvalidProxyCertificate sshProfile trustedHostKey acceptAnyHostKey jump preferHttp2 preferHttp3 knockHost authProbeHost");
            Fingerprint(proxy, "sshProfile", true);
            if (proxy["jump"] is JsonObject hop) Proxy(hop, true);
        }
        Keep(root, "schema version passwordsIncluded privateKeysIncluded activeProfileId diagnosticLogLimitMb tls ssh failover routing profiles subscription windows");
        Tls(root);
        if (root["ssh"] is JsonObject ssh)
        {
            Keep(ssh, "fingerprint authMode");
            Fingerprint(ssh, "fingerprint", true);
        }
        if (root["routing"] is JsonObject routing) Keep(routing, "bypassLocalNetworks");
        if (root["failover"] is JsonObject failover) Keep(failover, "mode profileIds");
        if (root["subscription"] is JsonObject subscription) Keep(subscription, "url username password intervalMinutes enabled fallbackUrls");
        if (root["windows"] is JsonObject windows)
        {
            if (windows["version"] is not null && windows["version"]!.GetValue<int>() != 1)
            {
                root.Remove("windows");
                if (warnings is not null && !warnings.Contains(UnknownWarning)) warnings.Add(UnknownWarning);
            }
            else
            {
                Keep(windows, "version defaultAction bypassLocalNetworks autoConnect resetExistingTcpConnections logSizeMb reverseDnsCacheHours resolveConnectionHosts showAllConnections language theme alwaysMinimizeToTray tlsFingerprint bootstrapDnsProvider launchAtSignIn rules tunMtu trafficUnits automaticAppUpdates appUpdatesThroughProxy");
                Fingerprint(windows, "tlsFingerprint");
            }
        }
        if (root["profiles"] is JsonArray profiles)
            foreach (var node in profiles)
            {
                var profile = node!.AsObject();
                // Read the legacy knock-host alias before discarding foreign platform settings.
                if (profile["proxy"] is JsonObject proxy)
                {
                    if (!proxy.ContainsKey("knockHost") && profile["browser"] is JsonObject browser && browser["knockHost"] is { } knock) proxy["knockHost"] = knock.DeepClone();
                    Proxy(proxy);
                }
                Keep(profile, "id name color countryCode proxy tls dns windows");
                Tls(profile);
                if (profile["dns"] is JsonObject dns) Keep(dns, "provider customDohUrl");
                if (profile["windows"] is JsonObject options) Keep(options, "sshHostKey sshJumpHostKey colorMode");
            }
        return root;
    }
}
