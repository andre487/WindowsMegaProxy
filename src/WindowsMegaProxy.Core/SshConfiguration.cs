using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class SshConfiguration
{
    public static readonly string[] Profiles = ["DEFAULT", "OPENSSH_TERMUX", "CONNECTBOT", "JUICESSH", "TERMIUS_ANDROID"];
    public static string PublicKey(ProxyProfile profile) => profile.Data["windows"] is JsonObject options ? options["sshHostKey"]?.GetValue<string>() ?? "" : "";

    public static string Fingerprint(string publicKey)
    {
        if (publicKey.Length > 16384) throw new ArgumentException(Ui.T("Enter an OpenSSH server public key."));
        var parts = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException(Ui.T("Enter an OpenSSH server public key."));
        byte[] data;
        try { data = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new ArgumentException(Ui.T("Enter an OpenSSH server public key.")); }
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(data) is not (> 0 and < 128) ||
            BinaryPrimitives.ReadUInt32BigEndian(data) > data.Length - 4 || Encoding.ASCII.GetString(data, 4, (int)BinaryPrimitives.ReadUInt32BigEndian(data)) != parts[0])
            throw new ArgumentException(Ui.T("Enter an OpenSSH server public key."));
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(data)).TrimEnd('=');
    }

    public static JsonObject Build(ProxyProfile profile, string tag, bool allowUnresolvedHostKey = false)
    {
        var proxy = profile.Proxy;
        string username = proxy["username"]?.GetValue<string>() ?? "";
        string password = proxy["password"]?.GetValue<string>() ?? "";
        string privateKey = (proxy["privateKey"]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException(Ui.T("Enter an SSH username."));
        if (password.Length == 0 && string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException(Ui.T("Enter an SSH password or private key."));
        bool acceptAny = proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false;
        string publicKey = PublicKey(profile);
        if (!acceptAny && !allowUnresolvedHostKey)
        {
            if (publicKey.Length == 0) throw new ArgumentException(Ui.T("The SSH server public key has not been verified."));
            if (Fingerprint(publicKey) != proxy["trustedHostKey"]?.GetValue<string>()) throw new ArgumentException(Ui.T("The SSH server key does not match the trusted fingerprint."));
        }
        string fingerprint = proxy["sshProfile"]?.GetValue<string>() ?? "DEFAULT";
        if (!Profiles.Contains(fingerprint)) throw new ArgumentException(Ui.T("Unsupported SSH fingerprint."));
        var result = new JsonObject
        {
            ["type"] = "ssh", ["tag"] = tag, ["server"] = profile.Host.Trim('[', ']'), ["server_port"] = profile.Port,
            ["user"] = username,
            ["client_version"] = fingerprint switch { "CONNECTBOT" => "SSH-2.0-ConnectBot", "JUICESSH" => "SSH-2.0-JuiceSSH", "TERMIUS_ANDROID" => "SSH-2.0-Termius", _ => "SSH-2.0-OpenSSH_8.4" },
            ["kex_algorithm"] = new JsonArray("curve25519-sha256", "ecdh-sha2-nistp256", "ecdh-sha2-nistp384", "ecdh-sha2-nistp521", "diffie-hellman-group-exchange-sha256", "diffie-hellman-group16-sha512", "diffie-hellman-group14-sha256"),
            ["cipher"] = new JsonArray("chacha20-poly1305@openssh.com", "aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "aes192-ctr", "aes256-ctr"),
            ["mac"] = new JsonArray("hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com", "hmac-sha2-256", "hmac-sha2-512")
        };
        if (!acceptAny && !allowUnresolvedHostKey) result["host_key"] = new JsonArray(publicKey);
        if (password.Length > 0) result["password"] = password;
        if (privateKey.Length > 0) result["private_key"] = privateKey;
        return result;
    }

    public static async Task<string> ReadHostKeyAsync(string host, int port, string trustedFingerprint, CancellationToken token, bool allowChangedKey = false)
    {
        if (host.Length == 0 || host.StartsWith('-') || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException(Ui.T("Enter a valid SSH host and port."));
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keyscan.exe");
        if (!File.Exists(executable)) throw new IOException(Ui.T("SSH key verification requires Windows OpenSSH Client. Install it in Windows Optional Features."));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-T", "5", "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", "ed25519,ecdsa,rsa", host.Trim('[', ']') }) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var scan = Process.Start(start) ?? throw new IOException(Ui.T("Could not read the SSH server key."));
        try
        {
            var output = ReadBoundedAsync(scan.StandardOutput, timeout.Token);
            var errors = ReadBoundedAsync(scan.StandardError, timeout.Token);
            await Task.WhenAll(output, errors, scan.WaitForExitAsync(timeout.Token));
            var keys = (await output).Split('\n').Where(line => !line.StartsWith('#')).Select(line => line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3).Select(parts => parts[1] + " " + parts[2]).ToArray();
            var key = keys.FirstOrDefault(value => Fingerprint(value) == trustedFingerprint)
                ?? (trustedFingerprint.Length == 0 || allowChangedKey ? keys.FirstOrDefault() : null);
            if (key is null) throw new IOException(Ui.T(trustedFingerprint.Length == 0 ? "Could not read the SSH server key." : "The SSH server key does not match the trusted fingerprint."));
            return key;
        }
        finally { if (!scan.HasExited) { scan.Kill(true); await scan.WaitForExitAsync(); } }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 65536) throw new IOException(Ui.T("The SSH key response exceeds the size limit."));
        }
        return text.ToString();
    }
}
