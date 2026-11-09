using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class SshConfiguration
{
    public static string AuthMode(Settings settings) => settings.SharedOptions["ssh"]?["authMode"]?.GetValue<string>() ?? "AUTO";
    public static bool ApplyKey(ProxyProfile? profile, SshHostKeyUpdate update)
    {
        if (profile is null || profile.Id != update.ProfileId || profile.Type is not ("SSH" or "SSH_JUMP") || update.Hop is not ("jump" or "destination") ||
            update.Hop == "jump" && profile.Type != "SSH_JUMP") return false;
        var endpoint = update.Hop == "jump" ? profile.Proxy["jump"]!.AsObject() : profile.Proxy;
        if (!string.Equals(endpoint["host"]!.ToString().Trim('[', ']'), update.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
            endpoint["port"]!.GetValue<int>() != update.Port || (endpoint["trustedHostKey"]?.GetValue<string>() ?? "") != update.PreviousFingerprint ||
            Fingerprint(update.PublicKey) != update.Fingerprint) return false;
        if (profile.Data["windows"] is not JsonObject) profile.Data["windows"] = new JsonObject();
        profile.Data["windows"]![update.Hop == "jump" ? "sshJumpHostKey" : "sshHostKey"] = update.PublicKey;
        endpoint["trustedHostKey"] = update.Fingerprint;
        return true;
    }

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

    public static JsonObject Build(ProxyProfile profile, string tag, string authMode = "AUTO")
    {
        if (authMode is not ("AUTO" or "PASSWORD_ONLY" or "KEY_ONLY")) throw new ArgumentException("Unsupported SSH authentication mode.");
        var proxy = profile.Proxy;
        string username = proxy["username"]?.GetValue<string>() ?? "";
        string password = authMode == "KEY_ONLY" ? "" : proxy["password"]?.GetValue<string>() ?? "";
        string privateKey = authMode == "PASSWORD_ONLY" ? "" : (proxy["privateKey"]?.GetValue<string>() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException(Ui.T("Enter an SSH username."));
        if (password.Length == 0 && string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException(Ui.T("Enter an SSH password or private key."));
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
        if (password.Length > 0) result["password"] = password;
        if (privateKey.Length > 0) result["private_key"] = privateKey;
        return result;
    }

}
