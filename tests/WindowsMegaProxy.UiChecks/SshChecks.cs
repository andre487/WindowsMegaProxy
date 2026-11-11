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
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", NetworkCorePaths.Client());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var profile = ProxyProfile.Create("SSH fixture");
            profile.Proxy["type"] = "SSH"; profile.Proxy["host"] = "127.0.0.1";
            profile.Proxy["port"] = fixture["port"]!.GetValue<int>();
            profile.Proxy["username"] = "test";
            profile.Data["windows"] = new JsonObject { ["sshHostKey"] = fixture["hostKey"]!.ToString() };
            profile.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(SshConfiguration.PublicKey(profile));
            foreach (string mode in new[] { "password", "key", "wrong-key", "wrong-password", "accept-new", "missing-public-key", "accept-change", "save-failure" })
            {
                using var scenarioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                profile.Proxy.Remove("password"); profile.Proxy.Remove("privateKey");
                profile.Proxy["password"] = mode == "wrong-password" ? "incorrect" : "secret";
                profile.Data["windows"]!["sshHostKey"] = fixture[mode is "wrong-key" or "accept-change" ? "otherKey" : "hostKey"]!.ToString();
                profile.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(SshConfiguration.PublicKey(profile));
                if (mode == "key") { profile.Proxy.Remove("password"); profile.Proxy["privateKey"] = fixture["privateKey"]!.ToString(); }
                if (mode is "accept-new" or "save-failure") profile.Proxy.Remove("trustedHostKey");
                if (mode is "accept-new" or "missing-public-key" or "save-failure") profile.Data["windows"]!.AsObject().Remove("sshHostKey");
                var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" };
                using var engine = new Engine(directory, allowEnvironmentOverrides: true);
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
                    return Task.CompletedTask;
                };
                try
                {
                    bool failed = false;
                    try
                    {
                        await engine.StartAsync(settings, true, scenarioTimeout.Token);
                        using var client = engine.CreateProbeClient(true);
                        Assert(await client.GetStringAsync(fixture["origin"]!.ToString(), scenarioTimeout.Token) == "ssh-fixture", "SSH forwards actual response bytes.");
                    }
                    catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException) { failed = true; }
                    Assert(failed == (mode == "wrong-password"), "Reject wrong credentials, but accept missing or changed host keys without direct fallback.");
                    Assert(confirmations == 0 && saves == 0, "Connecting must not scan, persist or ask about server keys.");
                    if (!failed) Assert(!engine.Http3Selected && await engine.CheckHttp3Async(scenarioTimeout.Token) == "", "SSH uses the stable core and hides HTTP/3 diagnostics.");
                }
                finally { await engine.StopAsync(); }
            }
            foreach (bool separateKey in new[] { false, true })
            {
                var chained = profile.Copy();
                chained.Proxy["type"] = "SSH_JUMP";
                chained.Proxy["password"] = "secret";
                chained.Proxy.Remove("trustedHostKey"); chained.Data.Remove("windows");
                chained.Proxy["jump"] = new JsonObject
                {
                    ["host"] = profile.Host, ["port"] = profile.Port, ["sameAuthentication"] = !separateKey,
                    ["username"] = "test", ["privateKey"] = separateKey ? fixture["privateKey"]!.ToString() : ""
                };
                var configured = new Settings { Profiles = [chained], ActiveProfileId = chained.Id, DefaultAction = "proxy" };
                using var engine = new Engine(directory, allowEnvironmentOverrides: true, otherRunning: () => false);
                var hops = new System.Collections.Generic.List<string>();
                engine.SaveSshHostKey = update => { hops.Add(update.Hop); return Task.CompletedTask; };
                try
                {
                    await engine.StartAsync(configured, testOnly: true, timeout.Token);
                    using var probe = engine.CreateProbeClient(true);
                    Assert(await probe.GetStringAsync(fixture["origin"]!.ToString(), timeout.Token) == "ssh-fixture", "Real SSH Jump forwards through both authenticated hops.");
                    Assert(hops.Count == 0, "Neither SSH hop requires key discovery or persistence.");
                }
                finally { await engine.StopAsync(); }
                Assert(Directory.GetFiles(directory, "ssh-jump-*.json").Length == 0, "Remove the temporary jump verification configuration.");
            }
            Assert(Directory.GetFiles(directory, "test-runtime-*.json").Length == 0, "Remove runtime configurations containing SSH credentials after shutdown.");
            File.WriteAllText(Path.Combine(directory, "result.txt"), "Real SSH forwarding and SSH Jump accept changed or missing server keys; authentication rejection and TCP-only diagnostics passed. No TUN or external traffic.");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); throw; }
    }
}
