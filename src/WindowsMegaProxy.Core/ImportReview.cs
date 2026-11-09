using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed record ImportOption(string ProfileId, string ProfileName, string Group);

public sealed class ImportReview
{
    private readonly Settings current;
    public ProfileImport Import { get; }
    public List<ImportOption> Options { get; } = [];
    public List<ProxyProfile> Missing { get; }
    public bool HasGlobalOptions { get; }
    public bool UnsafeTrust => Import.Profiles.Any(profile => Unsafe(profile.Proxy));
    private static bool Unsafe(JsonObject proxy) => new[] { "allowInvalidProxyCertificate", "acceptAnyHostKey" }.Any(key => proxy[key]?.GetValue<bool>() == true)
        || proxy["jump"] is JsonObject jump && Unsafe(jump);
    private static readonly string[] Groups = ["Connection", "Security", "DNS", "Routing"];

    public ImportReview(ProfileImport import, Settings current)
    {
        Import = import;
        this.current = current.Copy();
        ProfileTransfer.MatchListProfiles(import, current.Profiles, subscription: false);
        var candidate = PortableConfiguration.Import(import.Document.ToJsonString(), current);
        foreach (var profile in import.Profiles)
        {
            var old = current.Profiles.FirstOrDefault(item => item.Id == profile.Id);
            if (old is null || !import.Canonical) continue;
            foreach (string group in Groups)
                if (!JsonNode.DeepEquals(Fields(old.Data, group), Fields(candidate.Profiles.Single(item => item.Id == profile.Id).Data, group)))
                    Options.Add(new(profile.Id, profile.Name, group));
        }
        Missing = import.Canonical ? current.Profiles.Where(old => !import.Profiles.Any(profile => profile.Id == old.Id)).ToList() : [];
        HasGlobalOptions = import.Canonical && new[] { "windows", "tls", "ssh", "routing", "failover", "diagnosticLogLimitMb", "activeProfileId" }.Any(import.Document.ContainsKey);
    }

    private static JsonObject Fields(JsonObject profile, string group)
    {
        var proxy = profile["proxy"]!.AsObject();
        var result = new JsonObject();
        string[] keys = group switch
        {
            "Connection" => ["type", "port", "preferHttp2", "preferHttp3", "knockHost", "authProbeHost"],
            "Security" => ["allowInvalidProxyCertificate", "sshProfile", "trustedHostKey", "acceptAnyHostKey"],
            _ => []
        };
        foreach (string key in keys) if (proxy.ContainsKey(key)) result[key] = proxy[key]?.DeepClone();
        if (group is "Connection" or "Security" && proxy["jump"] is JsonObject jump)
        {
            var fields = new JsonObject();
            foreach (string key in group == "Connection" ? new[] { "port", "sameAuthentication" } : new[] { "allowInvalidProxyCertificate", "trustedHostKey", "acceptAnyHostKey" })
                if (jump.ContainsKey(key)) fields[key] = jump[key]?.DeepClone();
            result["jump"] = fields;
        }
        if (group == "Security" && profile["windows"] is JsonObject windows && windows.ContainsKey("sshHostKey")) result["windows:sshHostKey"] = windows["sshHostKey"]?.DeepClone();
        foreach (string key in group switch { "Security" => new[] { "tls" }, "DNS" => ["dns"], "Routing" => ["routing"], _ => [] })
            if (profile.ContainsKey(key)) result["profile:" + key] = profile[key]?.DeepClone();
        return result;
    }

    public Settings Apply(ISet<string> selected, bool applyGlobals, ISet<string> removeIds)
    {
        var document = (JsonObject)Import.Document.DeepClone();
        foreach (var node in document["profiles"]!.AsArray())
        {
            var profile = node!.AsObject();
            string id = profile["id"]!.ToString();
            var old = current.Profiles.FirstOrDefault(item => item.Id == id);
            if (old is null || !Import.Canonical) continue;
            foreach (string group in Groups)
            {
                if (selected.Contains(id + ":" + group)) continue;
                var incomingFields = Fields(profile, group);
                var localFields = Fields(old.Data, group);
                foreach (string key in incomingFields.Select(field => field.Key).Union(localFields.Select(field => field.Key)))
                {
                    if (key.StartsWith("profile:", StringComparison.Ordinal))
                    {
                        profile.Remove(key[8..]);
                        if (localFields.ContainsKey(key)) profile[key[8..]] = localFields[key]?.DeepClone();
                    }
                    else if (key.StartsWith("windows:", StringComparison.Ordinal))
                    {
                        profile["windows"] ??= new JsonObject();
                        profile["windows"]!.AsObject().Remove(key[8..]);
                        if (localFields.ContainsKey(key)) profile["windows"]![key[8..]] = localFields[key]?.DeepClone();
                    }
                    else if (key == "jump")
                    {
                        if (profile["proxy"]!["jump"] is not JsonObject jump) continue;
                        var local = localFields[key] as JsonObject ?? new JsonObject();
                        var incoming = incomingFields[key] as JsonObject ?? new JsonObject();
                        foreach (string field in incoming.Select(item => item.Key).Union(local.Select(item => item.Key)))
                        {
                            jump.Remove(field);
                            if (local.ContainsKey(field)) jump[field] = local[field]?.DeepClone();
                        }
                    }
                    else
                    {
                        profile["proxy"]!.AsObject().Remove(key);
                        if (localFields.ContainsKey(key)) profile["proxy"]![key] = localFields[key]?.DeepClone();
                    }
                }
            }
        }
        var result = PortableConfiguration.Import(document.ToJsonString(), current);
        if (!applyGlobals)
        {
            result.DefaultAction = current.DefaultAction; result.BypassLan = current.BypassLan;
            result.AutoConnect = current.AutoConnect; result.LogSizeMb = current.LogSizeMb;
            result.ShowAllConnections = current.ShowAllConnections; result.Language = current.Language; result.Theme = current.Theme;
            result.TlsFingerprint = current.TlsFingerprint; result.LaunchAtSignIn = current.LaunchAtSignIn;
            result.BootstrapDnsProvider = current.BootstrapDnsProvider;
            result.WindowsOptions = (JsonObject)current.WindowsOptions.DeepClone();
            result.Rules = current.Rules.Select(rule => rule.Copy()).ToList();
            result.ActiveProfileId = current.ActiveProfileId;
            foreach (string key in new[] { "tls", "ssh", "routing", "failover", "diagnosticLogLimitMb", "alwaysOnProfileId" })
            {
                result.SharedOptions.Remove(key);
                if (current.SharedOptions.ContainsKey(key)) result.SharedOptions[key] = current.SharedOptions[key]?.DeepClone();
            }
        }
        var allowed = Missing.Select(profile => profile.Id).ToHashSet();
        if (!removeIds.IsSubsetOf(allowed)) throw new ArgumentException("Only missing profiles can be removed during import.");
        result.Profiles.RemoveAll(profile => removeIds.Contains(profile.Id));
        ConfigurationSubscription.PruneFailover(result);
        if (result.ActiveProfile is null) result.ActiveProfileId = result.Profiles.FirstOrDefault()?.Id;
        ConfigurationSubscription.AdoptImport(result, current, Import.Document);
        PortableConfiguration.ValidateSettings(result);
        return result;
    }
}
