using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace WindowsMegaProxy;

public sealed record AppUpdatePackage(string Version, string FileName, string Url, long Size, string Sha256, byte[] Manifest, byte[] Signature);

public static class AppUpdateTrust
{
    public const string Repository = "andre487/WindowsMegaProxy";
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    public static Version CurrentVersion => typeof(AppUpdateTrust).Assembly.GetName().Version!;
    public static string CacheDirectory => Path.Combine(SettingsStore.DirectoryPath, "Updates");
    public static byte[] PublicKey
    {
        get
        {
            using var stream = typeof(AppUpdateTrust).Assembly.GetManifestResourceStream("WindowsMegaProxy.UpdatePublicKey")!;
            using var reader = new StreamReader(stream);
            return Convert.FromBase64String(reader.ReadToEnd().Trim());
        }
    }

    public static Version ParseVersion(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException("Invalid update version.");
        var version = Version.Parse(value);
        if (version.Major > 65535 || version.Minor > 65535 || version.Build > 65535) throw new InvalidDataException("Invalid update version.");
        return version;
    }

    public static AppUpdatePackage Verify(byte[] manifest, byte[] signature, byte[] publicKey)
    {
        if (manifest.Length is 0 or > 1024 * 1024 || signature.Length != Ed25519.SignatureSize || publicKey.Length != Ed25519.PublicKeySize ||
            !Ed25519.Verify(signature, 0, publicKey, 0, manifest, 0, manifest.Length))
            throw new CryptographicException("The update signature is invalid.");
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        string version = root.GetProperty("version").GetString()!;
        _ = ParseVersion(version);
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("preview").GetBoolean() ||
            root.GetProperty("platform").GetString() != "win-x64" ||
            root.GetProperty("signingKeyId").GetString() != Convert.ToHexStringLower(SHA256.HashData(publicKey)))
            throw new InvalidDataException("Unsupported update manifest.");
        var installer = root.GetProperty("components").GetProperty("installer");
        string file = $"WindowsMegaProxy-{version}-win-x64-setup.exe";
        string url = AssetUrl("v" + version, file);
        string hash = installer.GetProperty("sha256").GetString()!;
        long size = installer.GetProperty("size").GetInt64();
        if (installer.GetProperty("file").GetString() != file || installer.GetProperty("version").GetString() != version ||
            installer.GetProperty("url").GetString() != url || size is <= 0 or > MaximumInstallerSize ||
            !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid update installer.");
        return new(version, file, url, size, hash, manifest, signature);
    }

    public static string AssetUrl(string tag, string file) => $"https://github.com/{Repository}/releases/download/{tag}/{file}";
    public static bool IsNewer(string version, Version current) => ParseVersion(version) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    public static void CheckDirectory(string directory)
    {
        var info = Directory.CreateDirectory(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The update directory cannot be a link.");
    }

    public static async System.Threading.Tasks.Task VerifyInstallerAsync(string path, AppUpdatePackage package, System.Threading.CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != package.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != package.Sha256)
            throw new CryptographicException("The update installer hash is invalid.");
    }
}
