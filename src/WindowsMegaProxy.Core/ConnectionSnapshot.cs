using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

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
    public static TimeSpan PollInterval(int total) => TimeSpan.FromSeconds(total >= 1000 ? 5 : 2);

    public static ConnectionSnapshot Parse(JsonElement root, Settings settings, bool showAll)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid core connection snapshot.");
        var connections = Field(root, "connections");
        int total = connections.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? 0 : connections.GetArrayLength();
        var rows = new List<ConnectionRow>(Math.Min(total, 1000));
        string activeName = settings.ActiveProfile?.Name ?? Ui.T("Proxy");
        var profiles = settings.Profiles.ToDictionary(profile => profile.Id, profile => profile.Name);
        string Name(string tag) => tag switch
        {
            "direct" => Ui.T("System"), "proxy" => activeName,
            _ when tag.StartsWith("profile:", StringComparison.Ordinal) => profiles.GetValueOrDefault(tag[8..]) ?? Ui.T("Profile"),
            _ => tag
        };
        if (total > 0) foreach (var item in connections.EnumerateArray())
        {
            // The API explicitly marks fallback routes as final; equal outbounds can still match different rules.
            string rule = Text(item, "rule") ?? "";
            if (!showAll && rule == "final") continue;
            var metadata = Field(item, "metadata");
            var host = Text(metadata, "host");
            if (string.IsNullOrEmpty(host)) host = Text(metadata, "destinationIP");
            if (host?.Contains(':') == true) host = "[" + host + "]";
            var chains = Field(item, "chains");
            var tags = chains.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? [] : chains.EnumerateArray().Select(tag => tag.ToString()).ToArray();
            bool blocked = tags.Any(tag => tag is "block" or "reject") || rule == "reject" || rule.Contains("=> reject", StringComparison.OrdinalIgnoreCase);
            int group = blocked ? 1 : tags.Any(tag => tag == "proxy" || tag.StartsWith("profile:", StringComparison.Ordinal)) ? 0 : 2;
            string route = blocked ? Ui.T("Blocked") : string.Join(" → ", tags.Select(Name));
            long upload = Number(item, "upload"), download = Number(item, "download");
            rows.Add(new ConnectionRow(Text(metadata, "processPath") ?? "—", $"{host}:{Text(metadata, "destinationPort")}",
                Text(metadata, "network")?.ToUpperInvariant() ?? "—", route,
                Bytes(upload), Bytes(download), group, upload, download,
                Text(item, "id") ?? $"{Text(metadata, "sourceIP")}:{Text(metadata, "sourcePort")}/{host}:{Text(metadata, "destinationPort")}/{Text(metadata, "network")}/{Text(metadata, "processPath")}"));
            if (rows.Count == 1000) break;
        }
        return new(rows, total, Number(root, "uploadTotal"), Number(root, "downloadTotal"));
    }

    private static JsonElement Field(JsonElement parent, string name) =>
        parent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? default : parent.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement parent, string name) => Field(parent, name) is var value &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : null;
    private static long Number(JsonElement parent, string name) => Field(parent, name) is var value &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.GetInt64() : 0;

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
