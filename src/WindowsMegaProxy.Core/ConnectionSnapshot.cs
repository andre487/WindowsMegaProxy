using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed class ConnectionRow(string process, string destination, string network, string route, string upload, string download,
    int routeGroup = 2, long uploadedBytes = 0, long downloadedBytes = 0, string? id = null) : INotifyPropertyChanged
{
    public string Id { get; } = id ?? process + "|" + destination + "|" + network;
    public string Process { get; private set; } = process;
    public string Destination { get; private set; } = destination;
    public string Network { get; private set; } = network;
    public string Route { get; private set; } = route;
    public string Upload { get; private set; } = upload;
    public string Download { get; private set; } = download;
    public int RouteGroup { get; private set; } = routeGroup;
    public long UploadedBytes { get; private set; } = uploadedBytes;
    public long DownloadedBytes { get; private set; } = downloadedBytes;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ConnectionRow row)
    {
        if (Id != row.Id) throw new ArgumentException("Connection identifiers differ.");
        if (Process == row.Process && Destination == row.Destination && Network == row.Network && Route == row.Route &&
            Upload == row.Upload && Download == row.Download && RouteGroup == row.RouteGroup &&
            UploadedBytes == row.UploadedBytes && DownloadedBytes == row.DownloadedBytes) return;
        Process = row.Process; Destination = row.Destination; Network = row.Network; Route = row.Route;
        Upload = row.Upload; Download = row.Download; RouteGroup = row.RouteGroup;
        UploadedBytes = row.UploadedBytes; DownloadedBytes = row.DownloadedBytes;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
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
                item?["upload"]?.GetValue<long>() ?? 0, item?["download"]?.GetValue<long>() ?? 0,
                item?["id"]?.ToString() ?? $"{metadata?["sourceIP"]}:{metadata?["sourcePort"]}/{host}:{metadata?["destinationPort"]}/{metadata?["network"]}/{metadata?["processPath"]}"));
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
