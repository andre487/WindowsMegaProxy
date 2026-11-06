using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class SshChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        File.Delete(Path.Combine(directory, "failure.txt"));
        File.Delete(Path.Combine(directory, "result.txt"));
        try
        {
            var fixture = JsonNode.Parse(File.ReadAllText(path))!;
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", Path.GetFullPath(".tools/sing-box/sing-box.exe"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var profile = ProxyProfile.Create("SSH fixture");
            profile.Proxy["type"] = "SSH"; profile.Proxy["host"] = "127.0.0.1";
            profile.Proxy["port"] = fixture["port"]!.GetValue<int>();
            profile.Proxy["username"] = "test";
            profile.Data["windows"] = new JsonObject { ["sshHostKey"] = fixture["hostKey"]!.ToString() };
            profile.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(SshConfiguration.PublicKey(profile));
            string keyscan = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keyscan.exe");
            if (File.Exists(keyscan))
            {
                string scanned = await SshConfiguration.ReadHostKeyAsync(profile.Host, profile.Port, profile.Proxy["trustedHostKey"]!.ToString(), timeout.Token);
                Assert(SshConfiguration.Fingerprint(scanned) == profile.Proxy["trustedHostKey"]!.ToString(), "Read and verify the actual SSH server key against its SHA-256 fingerprint.");
                bool rejected = false;
                try { _ = await SshConfiguration.ReadHostKeyAsync(profile.Host, profile.Port, SshConfiguration.Fingerprint(fixture["otherKey"]!.ToString()), timeout.Token); }
                catch (IOException) { rejected = true; }
                Assert(rejected, "Key scanning must not silently replace an imported trusted fingerprint.");
            }
            foreach (string mode in new[] { "password", "key", "wrong-key", "wrong-password", "accept-new", "missing-public-key", "accept-change", "save-failure" })
            {
                profile.Proxy.Remove("password"); profile.Proxy.Remove("privateKey");
                profile.Proxy["password"] = mode == "wrong-password" ? "incorrect" : "secret";
                profile.Data["windows"]!["sshHostKey"] = fixture[mode is "wrong-key" or "accept-change" ? "otherKey" : "hostKey"]!.ToString();
                profile.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(SshConfiguration.PublicKey(profile));
                if (mode == "key") { profile.Proxy.Remove("password"); profile.Proxy["privateKey"] = fixture["privateKey"]!.ToString(); }
                if (mode is "accept-new" or "save-failure") profile.Proxy.Remove("trustedHostKey");
                if (mode is "accept-new" or "missing-public-key" or "save-failure") profile.Data["windows"]!.AsObject().Remove("sshHostKey");
                var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" };
                using var engine = new Engine(directory);
                int confirmations = 0, saves = 0;
                SshHostKeyUpdate? savedKey = null;
                engine.ConfirmSshHostKeyChange = update =>
                {
                    confirmations++;
                    Assert(update.PreviousFingerprint != update.Fingerprint, "Only changed server keys require confirmation.");
                    return Task.FromResult(mode == "accept-change");
                };
                engine.SaveSshHostKey = update =>
                {
                    if (mode == "save-failure") throw new IOException("Synthetic key persistence failure.");
                    saves++; savedKey = update;
                };
                try
                {
                    bool failed = false;
                    try
                    {
                        await engine.StartAsync(settings, true, timeout.Token);
                        using var client = engine.CreateProbeClient(true);
                        Assert(await client.GetStringAsync(fixture["origin"]!.ToString(), timeout.Token) == "ssh-fixture", "SSH forwards actual response bytes.");
                    }
                    catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException) { failed = true; }
                    Assert(failed == (mode.StartsWith("wrong-", StringComparison.Ordinal) || mode == "save-failure"), "Reject wrong keys, credentials and failed persistence without direct fallback.");
                    Assert(confirmations == (mode is "wrong-key" or "accept-change" ? 1 : 0), "Accept-new is automatic; changed keys require a decision.");
                    Assert(saves == (mode is "accept-new" or "missing-public-key" or "accept-change" ? 1 : 0), "Persist each accepted or resolved server key before connecting.");
                    if (savedKey is not null) Assert(engine.AppliedSettings!.ActiveProfile!.Proxy["trustedHostKey"]!.ToString() == savedKey.Fingerprint, "The applied snapshot contains the newly pinned server key.");
                    if (mode is "wrong-key" or "save-failure") Assert(!engine.Running, "Cancelled key changes or failed key persistence must not start the network core.");
                    if (!failed) Assert(!engine.Http3Selected && await engine.CheckHttp3Async(timeout.Token) == "", "SSH uses the stable core and hides HTTP/3 diagnostics.");
                }
                finally { await engine.StopAsync(); }
            }
            Assert(Directory.GetFiles(directory, "test-runtime-*.json").Length == 0, "Remove runtime configurations containing SSH credentials after shutdown.");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "Real SSH forwarding, accept-new, imported fingerprints, confirmed/cancelled key changes, persistence failure, authentication rejection and TCP-only diagnostics passed. No TUN or external traffic.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); throw; }
    }
}
