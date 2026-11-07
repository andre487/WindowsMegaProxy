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
    public static JsonObject Parse(IEnumerable<string> sources)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            if (Encoding.UTF8.GetByteCount(source) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Podkop source exceeds 4 MiB.");
            foreach (string raw in source.Split('\n'))
            {
                string line = raw.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                if (IPNetwork.TryParse(line, out _)) subnets.Add(line);
                else if (IPAddress.TryParse(line, out var address)) subnets.Add(address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
                else if (line.Length <= 253 && !line.Contains('*') && Uri.CheckHostName(line) == UriHostNameType.Dns) domains.Add(line.TrimEnd('.').ToLowerInvariant());
                else throw new InvalidDataException("Invalid Podkop list entry.");
                if (domains.Count + subnets.Count > 150000) throw new InvalidDataException("Podkop list contains too many entries.");
            }
        }
        if (domains.Count + subnets.Count == 0) throw new InvalidDataException("Empty Podkop list.");
        var rules = new JsonArray();
        if (domains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray(domains.Select(domain => (JsonNode)JsonValue.Create(domain)!).ToArray()) });
        if (subnets.Count > 0) rules.Add(new JsonObject { ["ip_cidr"] = new JsonArray(subnets.Select(subnet => (JsonNode)JsonValue.Create(subnet)!).ToArray()) });
        return new JsonObject { ["version"] = 3, ["rules"] = rules };
    }

    public async Task<string> GetAsync(string id, CancellationToken token, Func<string, CancellationToken, Task<string>>? download = null)
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
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(24)) return path;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        try
        {
            var sources = new List<string>();
            foreach (string relative in list.Paths)
            {
                string url = PodkopCatalog.BaseUrl + relative;
                sources.Add(await (download is null ? SubscriptionRefresh.DownloadAsync(new(url, [], null, null, 60, true), url, token) : download(url, token)).ConfigureAwait(false));
            }
            var parsed = Parse(sources);
            string text = parsed.ToJsonString();
            if (Encoding.UTF8.GetByteCount(text) > PortableConfiguration.MaximumBytes) throw new InvalidDataException("Podkop rule set exceeds 4 MiB.");
            SettingsStore.WriteAtomic(path, text);
            log?.Invoke("Podkop community list refreshed: " + id + ".");
            return path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            if (!cached) throw new IOException("A required Podkop list is unavailable and has no valid cached copy.");
            log?.Invoke("Podkop update failed; retaining the cached community list: " + id + ".");
            return path;
        }
    }
}
