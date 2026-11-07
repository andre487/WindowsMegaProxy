using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record SshHostKeyUpdate(string ProfileId, string ProfileName, string Host, int Port, string PreviousFingerprint, string Fingerprint, string PublicKey);
public sealed record ProbeAccess(int Port, string Secret);
public sealed record ServiceLogChunk(byte[] Data, bool Done = false, string? Error = null);
public sealed record ServiceStatus(bool Running, Settings? AppliedSettings, string? AppliedSignature, bool Http3Selected,
    bool UsedHttp3Fallback, string SelectedOutbound, bool HasProbeProfile, long Generation = 0);
public sealed record ServiceRequest(string Operation, Settings? Settings = null, bool TestOnly = false, bool ShowAll = false,
    string? Lease = null, SshHostKeyUpdate? HostKey = null, long? Revision = null);
public sealed record ServiceReply(bool CanEdit, Settings? Settings = null, ServiceStatus? Status = null,
    ConnectionSnapshot? Connections = null, string? Text = null, string? Error = null, SshHostKeyUpdate? HostKey = null,
    string? Lease = null, ProbeAccess? Probe = null, bool Initialized = true, bool AutoStart = false, long Revision = 0);

public static class ServiceProtocol
{
    public const string Name = "WindowsMegaProxy";
    public const string PipeName = "WindowsMegaProxy.Control.v1";
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (data.Length > MaximumFrameBytes) throw new IOException("Service message exceeds the size limit.");
        byte[] size = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        await stream.WriteAsync(size, token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token, Func<int>? authorize = null)
    {
        byte[] size = new byte[4];
        await stream.ReadExactlyAsync(size, token).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(size);
        int maximum = authorize?.Invoke() ?? MaximumFrameBytes;
        if (length <= 0 || length > maximum) throw new IOException("Invalid service message length.");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data, Options) ?? throw new IOException("Empty service message.");
    }
}

public static class ServiceView
{
    public static Settings PublicSettings(Settings source)
    {
        var result = new Settings
        {
            ActiveProfileId = source.ActiveProfileId, DefaultAction = source.DefaultAction, BypassLan = source.BypassLan,
            Language = source.Language, Theme = source.Theme, TlsFingerprint = source.TlsFingerprint,
            AutoConnect = source.AutoConnect, LaunchAtSignIn = source.LaunchAtSignIn, LogSizeMb = source.LogSizeMb,
            ShowAllConnections = source.ShowAllConnections,
            Rules = source.Rules.ConvertAll(rule => rule.Copy())
        };
        foreach (var profile in source.Profiles)
        {
            var visible = ProxyProfile.Create(profile.Name);
            visible.Data["id"] = profile.Id;
            foreach (string key in new[] { "color", "countryCode" })
                if (profile.Data[key] is { } value) visible.Data[key] = value.DeepClone();
            foreach (string key in new[] { "type", "host", "port", "preferHttp3" })
                if (profile.Proxy[key] is { } value) visible.Proxy[key] = value.DeepClone();
            if (profile.Proxy["jump"] is System.Text.Json.Nodes.JsonObject jump)
            {
                var endpoint = new System.Text.Json.Nodes.JsonObject();
                foreach (string key in new[] { "host", "port" })
                    if (jump[key] is { } value) endpoint[key] = value.DeepClone();
                visible.Proxy["jump"] = endpoint;
            }
            result.Profiles.Add(visible);
        }
        return result;
    }

    public static ServiceStatus? PublicStatus(ServiceStatus? status) => status is null ? null : status with
    { AppliedSettings = status.AppliedSettings is null ? null : PublicSettings(status.AppliedSettings), AppliedSignature = null };
}
