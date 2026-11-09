using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class PodkopCache(string directory, Action<string>? log = null)
{
    private readonly HashSet<string> unavailable = [];
    public IReadOnlySet<string> Unavailable => unavailable;
    public static JsonObject Parse(IEnumerable<string> sources, Action<int>? skipped = null)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int invalid = 0;
        foreach (string source in sources)
        {
            if (Encoding.UTF8.GetByteCount(source) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Podkop source exceeds 4 MiB.");
            foreach (string raw in source.Split('\n'))
            {
                string line = raw.Split('#', 2)[0].Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("*.", StringComparison.Ordinal)) line = line[2..];
                else if (line.StartsWith('.')) line = line[1..];
                if (IPNetwork.TryParse(line, out var network)) subnets.Add(network.ToString());
                else if (IPAddress.TryParse(line, out var address)) subnets.Add(address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
                else if (line.Length <= 253 && !line.Contains('*') && Uri.CheckHostName(line) == UriHostNameType.Dns) domains.Add(line.TrimEnd('.').ToLowerInvariant());
                else { invalid++; continue; }
                if (domains.Count + subnets.Count > 150000) throw new InvalidDataException("Podkop list contains too many entries.");
            }
        }
        if (invalid > 0) skipped?.Invoke(invalid);
        if (domains.Count + subnets.Count == 0) throw new InvalidDataException("Empty Podkop list.");
        var rules = new JsonArray();
        if (domains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray(domains.Select(domain => (JsonNode)JsonValue.Create(domain)!).ToArray()) });
        if (subnets.Count > 0) rules.Add(new JsonObject { ["ip_cidr"] = new JsonArray(subnets.Select(subnet => (JsonNode)JsonValue.Create(subnet)!).ToArray()) });
        return new JsonObject { ["version"] = 3, ["rules"] = rules };
    }

    public static JsonObject Merge(IEnumerable<JsonObject> sources)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (var rule in source["rules"]!.AsArray().OfType<JsonObject>())
                foreach (var (key, values) in new[] { ("domain_suffix", domains), ("ip_cidr", subnets) })
                    if (rule[key] is JsonArray entries)
                        foreach (var entry in entries) values.Add(entry!.GetValue<string>());
        var rules = new JsonArray();
        if (domains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray(domains.Order(StringComparer.Ordinal).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) });
        if (subnets.Count > 0) rules.Add(new JsonObject { ["ip_cidr"] = new JsonArray(subnets.Order(StringComparer.Ordinal).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) });
        return new JsonObject { ["version"] = 3, ["rules"] = rules };
    }

    public string MergeGroup(string[] ids)
    {
        ids = PodkopCatalog.Parse("podkop:" + string.Join(',', ids));
        if (ids.Length == 1) return Path.Combine(directory, ids[0] + ".json");
        var sources = ids.Order(StringComparer.Ordinal).Select(id => JsonNode.Parse(PortableConfiguration.ReadFile(Path.Combine(directory, id + ".json")))!.AsObject());
        string text = Merge(sources).ToJsonString();
        string path = Path.Combine(directory, PodkopCatalog.FileName(ids));
        if (!File.Exists(path) || File.ReadAllText(path) != text) SettingsStore.WriteAtomic(path, text);
        return path;
    }

    public async Task<string> GetAsync(string id, CancellationToken token, Func<string, CancellationToken, Task<string>>? download = null, bool cacheOnly = false)
    {
        var list = PodkopCatalog.Lists.Single(item => item.Id == id);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".json");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Podkop cache cannot be a reparse point.");
        bool cached = false;
        if (File.Exists(path))
        {
            try
            {
                var document = JsonNode.Parse(PortableConfiguration.ReadFile(path))!.AsObject();
                if (document["version"]?.GetValue<int>() != 3 || document["rules"]?.AsArray().Count is not > 0) throw new InvalidDataException();
                cached = true;
                unavailable.Remove(id);
                if (cacheOnly || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(24)) return path;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        try
        {
            if (cacheOnly) throw new IOException("Podkop refresh is waiting for the proxy connection.");
            var sources = new List<string>();
            foreach (string relative in list.Paths)
            {
                string url = PodkopCatalog.BaseUrl + relative;
                sources.Add(await (download is null ? SubscriptionRefresh.DownloadAsync(new(url, [], null, null, 60, true), url, token) : download(url, token)).ConfigureAwait(false));
            }
            var parsed = Parse(sources, count => log?.Invoke("Warning: Podkop list " + id + " skipped " + count + " malformed entries."));
            string text = parsed.ToJsonString();
            if (Encoding.UTF8.GetByteCount(text) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Podkop rule set exceeds 4 MiB.");
            SettingsStore.WriteAtomic(path, text);
            unavailable.Remove(id);
            log?.Invoke("Podkop community list refreshed: " + id + ".");
            return path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            if (!cached)
            {
                SettingsStore.WriteAtomic(path, new JsonObject { ["version"] = 3, ["rules"] = new JsonArray() }.ToJsonString());
                unavailable.Add(id);
                log?.Invoke("Warning: Podkop list " + id + " is unavailable without a verified cache (" + error.GetType().Name + "); its destination matches are inactive until an update succeeds.");
                return path;
            }
            log?.Invoke("Podkop update failed; retaining the cached community list: " + id + ".");
            return path;
        }
    }
}
