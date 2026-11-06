using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed record ConnectionRow(string Process, string Destination, string Network, string Route, string Upload, string Download, int RouteGroup = 2, long UploadedBytes = 0, long DownloadedBytes = 0);
public sealed record ConnectionSnapshot(IReadOnlyList<ConnectionRow> Rows, int Total, long Upload, long Download)
{
    public static ConnectionSnapshot Parse(JsonNode root, Settings settings, bool showAll)
    {
        var connections = root["connections"]?.AsArray() ?? new JsonArray();
        var rows = new List<ConnectionRow>();
        foreach (var item in connections)
        {
            // The API explicitly marks fallback routes as final; equal outbounds can still match different rules.
            if (!showAll && item?["rule"]?.ToString() == "final") continue;
            var metadata = item?["metadata"];
            var host = metadata?["host"]?.ToString();
            if (string.IsNullOrEmpty(host)) host = metadata?["destinationIP"]?.ToString();
            if (host?.Contains(':') == true) host = "[" + host + "]";
            var tags = (item?["chains"]?.AsArray() ?? new JsonArray()).Select(tag => tag?.ToString() ?? "").ToArray();
            string rule = item?["rule"]?.ToString() ?? "";
            bool blocked = tags.Any(tag => tag is "block" or "reject") || rule == "reject" || rule.Contains("=> reject", StringComparison.OrdinalIgnoreCase);
            int group = blocked ? 1 : tags.Any(tag => tag == "proxy" || tag.StartsWith("profile:", StringComparison.Ordinal)) ? 0 : 2;
            string route = blocked ? Ui.T("Blocked") : string.Join(" → ", tags.Select(tag => RouteName(tag, settings)));
            rows.Add(new ConnectionRow(metadata?["processPath"]?.ToString() ?? "—", $"{host}:{metadata?["destinationPort"]}",
                metadata?["network"]?.ToString()?.ToUpperInvariant() ?? "—", route,
                Bytes(item?["upload"]?.GetValue<long>() ?? 0), Bytes(item?["download"]?.GetValue<long>() ?? 0), group,
                item?["upload"]?.GetValue<long>() ?? 0, item?["download"]?.GetValue<long>() ?? 0));
            if (rows.Count == 1000) break;
        }
        return new(rows, connections.Count, root["uploadTotal"]?.GetValue<long>() ?? 0, root["downloadTotal"]?.GetValue<long>() ?? 0);
    }

    public static string RouteName(string tag, Settings settings) => tag switch
    {
        "direct" => Ui.T("System"),
        "proxy" => settings.ActiveProfile?.Name ?? Ui.T("Proxy"),
        _ when tag.StartsWith("profile:", StringComparison.Ordinal) => settings.Profiles.FirstOrDefault(profile => profile.Id == tag[8..])?.Name ?? Ui.T("Profile"),
        _ => tag
    };

    public static string Bytes(double value)
    {
        var units = new[] { Ui.T("B"), Ui.T("KiB"), Ui.T("MiB"), Ui.T("GiB") };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
