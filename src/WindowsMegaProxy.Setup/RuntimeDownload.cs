using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

internal static partial class Program
{
    private static async Task PrepareRuntime(SetupManifest manifest, string directory)
    {
        foreach (var group in manifest.RuntimeFiles.GroupBy(file => file.Package))
        {
            if (group.Key < 0 || group.Key >= manifest.Packages.Length) throw new IOException("Invalid runtime package index.");
            var missing = group.Where(file => Personal || !file.Path.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase))
                .Where(file => !CopyCachedRuntime(file, directory)).ToArray();
            if (missing.Length == 0) continue;
            string archive = Path.Combine(directory, "runtime-" + group.Key + ".nupkg");
            await DownloadRuntime(manifest.Packages[group.Key], archive);
            using (var zip = ZipFile.OpenRead(archive))
                foreach (var file in missing)
                {
                    string target = EntryPath(directory, file.Path);
                    if (!file.Entry.StartsWith("runtimes/win-x64/", StringComparison.Ordinal)) throw new IOException("Invalid runtime asset.");
                    var entry = zip.GetEntry(file.Entry) ?? throw new IOException("Incomplete Microsoft runtime package.");
                    if (entry.Length > 128L * 1024 * 1024) throw new IOException("Runtime file is too large.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                    if (!HashMatches(target, file.Sha256)) throw new CryptographicException("Runtime file checksum mismatch.");
                }
            File.Delete(archive);
            Console.WriteLine("Verified downloaded .NET runtime package.");
        }
    }

    private static bool CopyCachedRuntime(RuntimeFile file, string directory)
    {
        bool launcher = file.Path.StartsWith("launcher/", StringComparison.OrdinalIgnoreCase);
        string source = launcher ? EntryPath(LauncherDirectory, file.Path.Substring(9)) : EntryPath(Root, file.Path);
        string target = EntryPath(directory, file.Path);
        byte[]? bytes = null;
        void Read()
        {
            RejectReparseChain(source);
            if (!File.Exists(source) || new FileInfo(source).Length > 128L * 1024 * 1024) return;
            bytes = File.ReadAllBytes(source);
        }
        // User files are never opened with the elevated installer's authority.
        if (launcher) Unelevated(Read); else Read();
        if (bytes is null) return false;
        using var hash = SHA256.Create();
        if (!string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return true;
    }

    internal static bool OfficialRuntime(Uri uri) => uri.Scheme == "https" && uri.Host == "api.nuget.org" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.AbsolutePath.StartsWith("/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/v3-flatcontainer/microsoft.windowsdesktop.app.runtime.win-x64/", StringComparison.Ordinal)) &&
        uri.AbsolutePath.EndsWith(".nupkg", StringComparison.Ordinal);

    private static async Task DownloadRuntime(RuntimePackage package, string destination)
    {
        var uri = new Uri(package.Url);
        if (!OfficialRuntime(uri)) throw new IOException("Unexpected Microsoft runtime source.");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
                Console.WriteLine("Downloading .NET runtime package; attempt " + (attempt + 1) + ".");
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("Insecure runtime redirect.");
                if ((int)response.StatusCode >= 500 && attempt < 3)
                {
                    Console.Error.WriteLine("Microsoft runtime server returned " + (int)response.StatusCode + " " + response.ReasonPhrase);
                    await Task.Delay(1000 << attempt);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new IOException("Microsoft runtime server returned " + (int)response.StatusCode + " " + response.ReasonPhrase);
                using var input = await response.Content.ReadAsStreamAsync();
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token)) != 0)
                    {
                        if ((total += read) > 200L * 1024 * 1024) throw new IOException("Runtime archive is too large.");
                        await output.WriteAsync(buffer, 0, read, deadline.Token);
                    }
                }
                if (!HashMatches(destination, package.Sha256)) throw new CryptographicException("Microsoft runtime package checksum mismatch.");
                return;
            }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(1000 << attempt); }
            catch (OperationCanceledException) when (attempt < 3) { await Task.Delay(1000 << attempt); }
        }
    }
}
