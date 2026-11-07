using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Math.EC.Rfc8032;
using WindowsMegaProxy;

internal static class AppUpdateChecks
{
    internal sealed class Feed : HttpMessageHandler
    {
        private readonly byte[] seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        public byte[] Key { get; } = new byte[32];
        public string Version { get; set; } = "0.0.2";
        public bool Unavailable { get; set; }
        public bool MissingInstaller { get; set; }
        public bool BadSignature { get; set; }
        public bool CorruptPayload { get; set; }
        public bool UntrustedRedirect { get; set; }
        public int Downloads { get; private set; }
        public int Requests { get; private set; }
        public DateTimeOffset Now { get; set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public CancellationTokenSource? CancelDownload { get; set; }
        public Feed() => Ed25519.GeneratePublicKey(seed, 0, Key, 0);
        public byte[] Payload => Encoding.UTF8.GetBytes("MZ synthetic installer " + Version);
        public string FileName => $"WindowsMegaProxy-{Version}-win-x64-setup.exe";
        public AppUpdateManager Manager(string directory, Version? installed = null) => new(directory, installed ?? new Version(0, 0, 1), Key,
            new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }, () => Now);

        private byte[] Manifest()
        {
            var root = new JsonObject
            {
                ["schemaVersion"] = 1, ["preview"] = false, ["platform"] = "win-x64", ["version"] = Version,
                ["signingKeyId"] = Convert.ToHexStringLower(SHA256.HashData(Key)),
                ["components"] = new JsonObject { ["installer"] = new JsonObject
                {
                    ["version"] = Version, ["file"] = FileName, ["url"] = AppUpdateTrust.AssetUrl("v" + Version, FileName),
                    ["size"] = Payload.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Payload))
                }}
            };
            return JsonSerializer.SerializeToUtf8Bytes(root);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            HttpResponseMessage Reply(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            string path = request.RequestUri!.AbsolutePath;
            if (Unavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.RequestUri.Host == "api.github.com")
            {
                var names = new[] { "update-manifest.json", "update-manifest.sig" }.Concat(MissingInstaller ? [] : new[] { FileName });
                var assets = new JsonArray(names.Select(name => (JsonNode)new JsonObject
                { ["name"] = name, ["state"] = "uploaded", ["browser_download_url"] = AppUpdateTrust.AssetUrl("v" + Version, name) }).ToArray());
                return Task.FromResult(Reply(JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["tag_name"] = "v" + Version, ["draft"] = false, ["prerelease"] = false, ["assets"] = assets })));
            }
            if (path.EndsWith("update-manifest.json", StringComparison.Ordinal)) return Task.FromResult(Reply(Manifest()));
            if (path.EndsWith("update-manifest.sig", StringComparison.Ordinal))
            {
                byte[] manifest = Manifest(), signature = new byte[64];
                Ed25519.Sign(seed, 0, manifest, 0, manifest.Length, signature, 0);
                if (BadSignature) signature[0] ^= 1;
                return Task.FromResult(Reply(signature));
            }
            Downloads++;
            if (UntrustedRedirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://untrusted.example/installer.exe");
                return Task.FromResult(redirect);
            }
            if (CancelDownload is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(Payload, CancelDownload)) });
            byte[] payload = Payload;
            if (CorruptPayload) payload[0] ^= 1;
            return Task.FromResult(Reply(payload));
        }
    }

    private sealed class CancelStream(byte[] content, CancellationTokenSource cancellation) : MemoryStream(content)
    {
        private bool started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!started) { started = true; return base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], token); }
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var now = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        Check(AppUpdatePolicy.Enabled(new Settings()), "Automatic updates default to enabled.");
        Check(AppUpdatePolicy.CheckDue(default, now) && !AppUpdatePolicy.CheckDue(now.AddHours(-23), now) && AppUpdatePolicy.CheckDue(now.AddDays(-1), now), "Daily checks survive application restarts.");
        Check(!AppUpdatePolicy.NotificationDue("0.0.2", "0.0.2", now.AddDays(-6), null, now) &&
            AppUpdatePolicy.NotificationDue("0.0.2", "0.0.2", now.AddDays(-7), null, now) &&
            AppUpdatePolicy.NotificationDue("0.0.3", "0.0.2", now, null, now) &&
            !AppUpdatePolicy.NotificationDue("0.0.2", "0.0.2", now.AddDays(-20), "0.0.2", now), "Ignored notifications repeat weekly or for a new version; acknowledged versions stay quiet.");
        Check(AppUpdateTrust.PublicKey.Length == 32, "The production update verification key is pinned in the runtime.");
        string root = Path.GetFullPath("artifacts/application-updates/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var feed = new Feed();
        string cache = Path.Combine(root, "cache");
        using (var manager = feed.Manager(cache))
        {
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "orphaned-old-version.exe"), "orphan");
            feed.Unavailable = true;
            await manager.CheckAsync(default);
            Check(manager.StatusKey == "Update files are unavailable." && !manager.Ready && !manager.CheckDue, "No GitHub release is an explicit unavailable-files result and does not cause a retry loop.");
            Check(!Directory.EnumerateFiles(cache, "*.exe").Any(), "Orphaned updates are removed.");
            feed.Unavailable = false;
            await manager.CheckAsync(default);
            Check(manager.Ready && manager.NotificationDue && feed.Downloads == 1, "A valid signed release is downloaded and becomes installable.");
            await manager.MarkNotificationAsync(false, default);
            Check(!manager.NotificationDue, "A displayed notification is not repeated immediately.");
            await manager.CheckAsync(default);
            Check(feed.Downloads == 1, "Repeated checks reuse a verified installer instead of downloading duplicates.");
            File.WriteAllText(manager.InstallerPath, "corrupted cached installer");
            await manager.CheckAsync(default);
            Check(manager.Ready && feed.Downloads == 2, "A damaged cached installer is discarded and downloaded again.");
            feed.Now = feed.Now.AddDays(7);
            Check(manager.NotificationDue, "An ignored notification becomes due after one week.");
            feed.Version = "0.0.3";
            await manager.CheckAsync(default);
            Check(manager.Package!.Version == "0.0.3" && Directory.EnumerateFiles(cache, "*.exe").Count() == 1 && !Directory.EnumerateFiles(cache, "*.part").Any(), "Only the newest installer remains in the update cache.");
            await manager.MarkNotificationAsync(true, default);
            Check(!manager.NotificationDue, "Opening the ready update acknowledges the version.");
        }
        using (var restarted = feed.Manager(cache))
        {
            await restarted.InitializeAsync(default);
            Check(restarted.Ready && !restarted.NotificationDue && !restarted.CheckDue, "Verified state and acknowledgement survive a client restart.");
        }
        using (var upgraded = feed.Manager(cache, new Version(0, 0, 3)))
        {
            await upgraded.InitializeAsync(default);
            Check(!upgraded.Ready && !Directory.EnumerateFiles(cache, "*.exe").Any(), "A successfully installed version removes its cached installer.");
        }
        foreach (string failure in new[] { "signature", "payload", "redirect", "missing" })
        {
            using var invalid = new Feed { BadSignature = failure == "signature", CorruptPayload = failure == "payload", UntrustedRedirect = failure == "redirect", MissingInstaller = failure == "missing" };
            string path = Path.Combine(root, failure);
            using var manager = invalid.Manager(path);
            await manager.CheckAsync(default);
            Check(!manager.Ready && !Directory.EnumerateFiles(path, "*.exe*").Any(), "Invalid or missing updates never leave an installable or partial payload: " + failure);
            if (failure == "signature") Check(invalid.Downloads == 0, "An untrusted manifest cannot cause an installer download.");
        }
        using (var stopping = new CancellationTokenSource())
        using (var interrupted = new Feed { CancelDownload = stopping })
        using (var manager = interrupted.Manager(Path.Combine(root, "cancelled")))
        {
            try { await manager.CheckAsync(stopping.Token); throw new Exception("Update cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Check(!Directory.EnumerateFiles(Path.Combine(root, "cancelled"), "*.exe*").Any() && manager.CheckDue, "Cancelled downloads are deleted and retried after the next launch.");
        }
        var profile = ProxyProfile.Create("Update preference fixture"); profile.Proxy["host"] = "proxy.invalid";
        var saved = new Settings { Profiles = [profile], ActiveProfileId = profile.Id }; saved.WindowsOptions[AppUpdatePolicy.Option] = false;
        var exported = PortableConfiguration.Export(saved);
        Check(!AppUpdatePolicy.Enabled(PortableConfiguration.Import(exported.ToJsonString(), new Settings())), "The automatic-update preference survives MegaProxy JSON export/import.");
        Console.WriteLine("Signed application updates, missing releases, cache cleanup, notification scheduling and cancellation passed; no installer executed.");
    }
}
