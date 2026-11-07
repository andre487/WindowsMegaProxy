using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record AppUpdateState
{
    public DateTimeOffset LastCheck { get; init; }
    public DateTimeOffset LastNotification { get; init; }
    public string? NotifiedVersion { get; init; }
    public string? AcknowledgedVersion { get; init; }
    public byte[]? Manifest { get; init; }
    public byte[]? Signature { get; init; }
}

public sealed class AppUpdateManager : IDisposable
{
    private readonly string directory;
    private readonly Version currentVersion;
    private readonly byte[] key;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim gate = new(1);
    private bool initialized;
    public AppUpdateState State { get; private set; } = new();
    public AppUpdatePackage? Package { get; private set; }
    public string StatusKey { get; private set; } = "Updates have not been checked yet.";
    public string? ErrorKey { get; private set; }
    public int Progress { get; private set; }
    public bool Ready => Package is not null && File.Exists(InstallerPath);
    public string InstallerPath => Path.Combine(directory, "setup.exe");
    public bool CheckDue => AppUpdatePolicy.CheckDue(State.LastCheck, now());
    public bool NotificationDue => Ready && AppUpdatePolicy.NotificationDue(Package!.Version, State.NotifiedVersion, State.LastNotification, State.AcknowledgedVersion, now());
    public event Action? Changed;
    public event Action<string>? Log;

    public AppUpdateManager(string? directory = null, Version? currentVersion = null, byte[]? publicKey = null, HttpClient? client = null, Func<DateTimeOffset>? now = null)
    {
        this.directory = directory ?? AppUpdateTrust.CacheDirectory;
        this.currentVersion = currentVersion ?? AppUpdateTrust.CurrentVersion;
        key = publicKey ?? AppUpdateTrust.PublicKey;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        ownsClient = client is null;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await InitializeCoreAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task InitializeCoreAsync(CancellationToken token)
    {
        if (initialized) return;
        await Task.Run(async () =>
        {
            AppUpdateTrust.CheckDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            try
            {
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 2 * 1024 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                    State = JsonSerializer.Deserialize<AppUpdateState>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) ?? new();
                }
                if (State.Manifest is not null && State.Signature is not null)
                {
                    var package = AppUpdateTrust.Verify(State.Manifest, State.Signature, key);
                    if (AppUpdateTrust.IsNewer(package.Version, currentVersion) && File.Exists(InstallerPath))
                    {
                        await AppUpdateTrust.VerifyInstallerAsync(InstallerPath, package, token).ConfigureAwait(false);
                        Package = package; StatusKey = "Update ready to install.";
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                State = new();
                Log?.Invoke("Discarded invalid application update cache: " + error.GetType().Name + ".");
            }
            ClearFiles(keepInstaller: Ready);
            if (!Ready) State = State with { Manifest = null, Signature = null };
            Save(); initialized = true;
        }, token).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task CheckAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(token).ConfigureAwait(false);
            State = State with { LastCheck = now() };
            ErrorKey = null; Progress = 0; StatusKey = "Checking for updates…"; Changed?.Invoke();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                byte[] releaseBytes = await GetBytesAsync(new Uri($"https://api.github.com/repos/{AppUpdateTrust.Repository}/releases/latest"), 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                using var release = JsonDocument.Parse(releaseBytes);
                var root = release.RootElement;
                string tag = root.GetProperty("tag_name").GetString()!;
                if (!tag.StartsWith('v') || root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new UpdateUnavailableException();
                Version version = AppUpdateTrust.ParseVersion(tag[1..]);
                if (version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)))
                {
                    Package = null; ClearFiles(false); State = State with { Manifest = null, Signature = null };
                    StatusKey = "The application is up to date.";
                    return;
                }
                var assets = root.GetProperty("assets").EnumerateArray().ToArray();
                Uri Asset(string name)
                {
                    var matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
                    if (matches.Length != 1 || matches[0].GetProperty("state").GetString() != "uploaded") throw new UpdateUnavailableException();
                    string expected = AppUpdateTrust.AssetUrl(tag, name);
                    if (matches[0].GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("Invalid update asset URL.");
                    return new Uri(expected);
                }
                byte[] manifest = await GetBytesAsync(Asset("update-manifest.json"), 1024 * 1024, deadline.Token).ConfigureAwait(false);
                byte[] signature = await GetBytesAsync(Asset("update-manifest.sig"), 64, deadline.Token).ConfigureAwait(false);
                var package = await Task.Run(() => AppUpdateTrust.Verify(manifest, signature, key), deadline.Token).ConfigureAwait(false);
                if ("v" + package.Version != tag) throw new InvalidDataException("Release and update versions differ.");
                _ = Asset(package.FileName);
                bool cached = false;
                if (Ready && Package!.Sha256 == package.Sha256)
                {
                    try { await AppUpdateTrust.VerifyInstallerAsync(InstallerPath, package, deadline.Token).ConfigureAwait(false); cached = true; }
                    catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
                    { Package = null; Log?.Invoke("Discarded a damaged cached application update."); }
                }
                if (!cached)
                {
                    Package = null; State = State with { Manifest = null, Signature = null };
                    ClearFiles(false);
                    StatusKey = "Downloading update…"; Changed?.Invoke();
                    await DownloadAsync(package, deadline.Token).ConfigureAwait(false);
                }
                Package = package;
                State = State with { Manifest = manifest, Signature = signature };
                StatusKey = "Update ready to install.";
                Log?.Invoke("Application update " + package.Version + " downloaded and verified.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                State = State with { LastCheck = default };
                throw;
            }
            catch (Exception error)
            {
                ErrorKey = error is UpdateUnavailableException ? "Update files are unavailable." :
                    error is CryptographicException or InvalidDataException or JsonException or FormatException ? "Update verification failed." : "Could not check for updates. Try again later.";
                if (error is UpdateUnavailableException)
                {
                    Package = null; State = State with { Manifest = null, Signature = null }; ClearFiles(false);
                }
                StatusKey = Ready ? "Update ready to install." : ErrorKey;
                Log?.Invoke("Application update check failed: " + error.GetType().Name + ".");
            }
            finally { File.Delete(InstallerPath + ".part"); Save(); Changed?.Invoke(); }
        }
        finally { gate.Release(); }
    }

    public async Task MarkNotificationAsync(bool acknowledged, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Ready) return;
            if (acknowledged && State.AcknowledgedVersion == Package!.Version) return;
            State = acknowledged ? State with { AcknowledgedVersion = Package!.Version } :
                State with { NotifiedVersion = Package!.Version, LastNotification = now() };
            await Task.Run(Save, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void ClearFiles(bool keepInstaller)
    {
        foreach (string path in Directory.EnumerateFiles(directory))
            if (Path.GetFileName(path) != "state.json" && (!keepInstaller || path != InstallerPath)) File.Delete(path);
    }
    private void Save() => SettingsStore.WriteAtomic(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(State));

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Port != 443 || uri.Host is not
                ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                throw new InvalidDataException("Untrusted update download URL.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WindowsMegaProxy/" + currentVersion.ToString(3));
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Invalid update redirect.");
                uri = new Uri(uri, location);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); throw new UpdateUnavailableException(); }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Too many update download redirects.");
    }

    private async Task<byte[]> GetBytesAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await GetAsync(uri, token).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var target = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (target.Length + read > limit) throw new InvalidDataException("Update metadata exceeds its size limit.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    private async Task DownloadAsync(AppUpdatePackage package, CancellationToken token)
    {
        using var response = await GetAsync(new Uri(package.Url), token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length != package.Size) throw new InvalidDataException("Invalid update download size.");
        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var target = new FileStream(InstallerPath + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[65536]; long total = 0; int read;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > package.Size) throw new InvalidDataException("Update download exceeds its signed size.");
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                int progress = (int)(total * 100 / package.Size);
                if (progress != Progress) { Progress = progress; Changed?.Invoke(); }
            }
            if (total != package.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != package.Sha256)
                throw new CryptographicException("The update download failed its integrity check.");
        }
        File.Move(InstallerPath + ".part", InstallerPath);
    }

    public void Dispose() { if (ownsClient) client.Dispose(); gate.Dispose(); }
    private sealed class UpdateUnavailableException : IOException { }
}
