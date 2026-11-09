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
        if (Process != row.Process) { Process = row.Process; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Process))); }
        if (Destination != row.Destination) { Destination = row.Destination; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Destination))); }
        if (Network != row.Network) { Network = row.Network; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Network))); }
        if (Route != row.Route) { Route = row.Route; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Route))); }
        if (Upload != row.Upload) { Upload = row.Upload; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Upload))); }
        if (Download != row.Download) { Download = row.Download; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Download))); }
        if (RouteGroup != row.RouteGroup) { RouteGroup = row.RouteGroup; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RouteGroup))); }
        if (UploadedBytes != row.UploadedBytes) { UploadedBytes = row.UploadedBytes; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadedBytes))); }
        if (DownloadedBytes != row.DownloadedBytes) { DownloadedBytes = row.DownloadedBytes; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadedBytes))); }

    }
}
public sealed record ConnectionSnapshot(IReadOnlyList<ConnectionRow> Rows, int Total, long Upload, long Download)
{
    public static ConnectionSnapshot Parse(JsonNode root, Settings settings, bool showAll)
    {
        var connections = root["connections"]?.AsArray() ?? new JsonArray();
        var rows = new List<ConnectionRow>();
        string activeName = settings.ActiveProfile?.Name ?? Ui.T("Proxy");
        var profiles = settings.Profiles.ToDictionary(profile => profile.Id, profile => profile.Name);
        string Name(string tag) => tag switch
        {
            "direct" => Ui.T("System"), "proxy" => activeName,
            _ when tag.StartsWith("profile:", StringComparison.Ordinal) => profiles.GetValueOrDefault(tag[8..]) ?? Ui.T("Profile"),
            _ => tag
        };
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
            string route = blocked ? Ui.T("Blocked") : string.Join(" → ", tags.Select(Name));
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

    public static string Bytes(double value, string units = "IEC")
    {
        int unit = 0;
        int divisor = units == "SI" ? 1000 : 1024;
        while (value >= divisor && unit < 3) { value /= divisor; unit++; }
        string[] labels = units == "SI" ? ["B", "KB", "MB", "GB"] : ["B", "KiB", "MiB", "GiB"];
        return $"{value:0.#} {labels[unit]}";
    }
}
