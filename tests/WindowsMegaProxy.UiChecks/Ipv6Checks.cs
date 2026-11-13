using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class Ipv6Checks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(string fixturePath)
    {
        string directory = Path.GetDirectoryName(fixturePath)!;
        File.Delete(Path.Combine(directory, "result.txt"));
        File.Delete(Path.Combine(directory, "failure.txt"));
        try { await CheckAsync(directory, JsonNode.Parse(File.ReadAllText(fixturePath))!); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); throw; }
    }

    private static async Task CheckAsync(string directory, JsonNode fixture)
    {
        Assert(Socket.OSSupportsIPv6, "IPv6 loopback is required; do not silently skip this suite.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string certPath = Path.Combine(directory, "certificate.pem"), keyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        string stable = Path.GetFullPath(".tools/sing-box/sing-box.exe"), alpha = Path.GetFullPath(".tools/sing-box-h3/sing-box.exe");
        var reservation = Http3Checks.ReserveProxyPort(IPAddress.IPv6Loopback);
        using var tcp = reservation.Tcp;
        using var udp = reservation.Udp;
        int httpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using var socksReservation = new TcpListener(IPAddress.IPv6Loopback, 0);
        socksReservation.Start();
        int socksPort = ((IPEndPoint)socksReservation.LocalEndpoint).Port;
        var serverConfig = new JsonObject
        {
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "http", ["listen"] = "::1", ["listen_port"] = httpPort, ["version"] = new JsonArray(1, 2, 3),
                    ["users"] = new JsonArray(new JsonObject { ["username"] = "test", ["password"] = "secret" }),
                    ["tls"] = new JsonObject { ["enabled"] = true, ["certificate_path"] = certPath, ["key_path"] = keyPath }
                },
                new JsonObject { ["type"] = "socks", ["listen"] = "::1", ["listen_port"] = socksPort,
                    ["users"] = new JsonArray(new JsonObject { ["username"] = "test", ["password"] = "secret" }) }),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["rules"] = new JsonArray(
                new JsonObject { ["ip_cidr"] = new JsonArray("127.0.0.1/32", "::1/128"), ["action"] = "route", ["outbound"] = "direct" },
                new JsonObject { ["action"] = "reject" }) }
        };
        tcp.Stop(); udp.Dispose(); socksReservation.Stop();
        await using var server = await Core.StartAsync(alpha, serverConfig, directory, "servers", IPAddress.IPv6Loopback, httpPort, token);
        foreach (var (type, version) in new[] { ("SOCKS5", 1), ("HTTPS", 1), ("HTTPS", 2), ("HTTPS", 3),
            ("HTTPS_JUMP", 1), ("HTTPS_JUMP", 2), ("HTTPS_JUMP", 3), ("MASQUE", 3), ("SSH", 1), ("SSH_JUMP", 1) })
        {
            var profile = ProxyProfile.Create(type);
            profile.Proxy["type"] = type;
            profile.Proxy["host"] = "::1";
            profile.Proxy["port"] = type.StartsWith("SSH", StringComparison.Ordinal) ? fixture["port"]!.GetValue<int>() : type == "SOCKS5" ? socksPort : httpPort;
            profile.Proxy["username"] = "test"; profile.Proxy["password"] = "secret";
            profile.Proxy["allowInvalidProxyCertificate"] = true;
            profile.Proxy["preferHttp2"] = version >= 2; profile.Proxy["preferHttp3"] = version == 3;
            if (type.EndsWith("_JUMP", StringComparison.Ordinal)) profile.Proxy["jump"] = new JsonObject
            { ["host"] = "::1", ["port"] = profile.Port, ["sameAuthentication"] = true, ["allowInvalidProxyCertificate"] = true };
            foreach (string policy in new[] { "allow", "deny", "rule-proxy", "rule-proxy-deny", "deny-compatible", "rule-system", "rule-block", "bad-auth" })
            {
                bool allowed = policy is "allow" or "rule-proxy" or "rule-system";
                profile.Data["routing"] = new JsonObject { ["allowIpv6"] = policy is "allow" or "rule-proxy" or "bad-auth" };
                if (policy == "rule-system") profile.Data["routing"]!["allowIpv6"] = true;
                profile.Proxy["password"] = policy == "bad-auth" ? "incorrect" : "secret";
                var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy", BypassLan = false, SystemAllowIpv6 = true, BlockDisabledIpv6 = policy != "deny-compatible" };
                if (policy.StartsWith("rule-", StringComparison.Ordinal))
                {
                    settings.DefaultAction = policy is "rule-proxy" or "rule-proxy-deny" ? "system" : "proxy";
                    settings.Rules.Add(new RoutingRule { Source = "all", Destination = "::1/128", Action = policy == "rule-system" ? "system" : policy == "rule-block" ? "block" : "proxy" });
                }
                using var inboundReservation = new TcpListener(IPAddress.Loopback, 0);
                inboundReservation.Start();
                int port = ((IPEndPoint)inboundReservation.LocalEndpoint).Port;
                var config = Configuration.Build(settings, 1, "fixture");
                // Keep production routing/outbounds. Replace only the privileged TUN ingress with local SOCKS.
                config["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["listen"] = "127.0.0.1", ["listen_port"] = port,
                    ["users"] = new JsonArray(new JsonObject { ["username"] = "diagnostic", ["password"] = "fixture" }) });
                config.Remove("experimental");
                string name = $"{type}-h{version}-{policy}";
                inboundReservation.Stop();
                await using var core = await Core.StartAsync(NetworkCorePaths.Client(Configuration.RequiresAlpha(settings)), config, directory, name, IPAddress.Loopback, port, token);
                using var client = new HttpClient(new HttpClientHandler
                { Proxy = new WebProxy($"socks5://127.0.0.1:{port}") { Credentials = new NetworkCredential("diagnostic", "fixture") }, UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(10) };
                bool delivered = false;
                try { delivered = await client.GetStringAsync(fixture["origin6"]!.ToString(), token) == "ssh-fixture"; }
                catch (HttpRequestException) when (!allowed) { }
                Assert(delivered == allowed, name + ": IPv6 routing policy must be enforced by the actual core.");
                bool ipv4Delivered = false;
                try { ipv4Delivered = await client.GetStringAsync(fixture["origin"]!.ToString(), token) == "ssh-fixture"; }
                catch (HttpRequestException) when (policy == "bad-auth") { }
                Assert(ipv4Delivered == (policy != "bad-auth"), name + ": IPv4 still works unless proxy authentication fails.");
                if (policy != "bad-auth" && (type == "SOCKS5" || version == 3))
                {
                    await Http3Checks.CheckUdpAsync(config, token);
                    await Http3Checks.CheckUdpAsync(config, token, IPAddress.IPv6Loopback, allowed);
                }
                Console.WriteLine("IPv6 passed: " + name);
            }
        }
        File.WriteAllText(Path.Combine(directory, "result.txt"), "IPv6 proxy endpoints, both Jump hops, IPv4/IPv6 destinations, allow/deny and explicit proxy/system/block rules passed for all six profile types, including HTTPS H1/H2/H3. IPv6 UDP passed for SOCKS5 and H3 transports. Real pinned cores; no TUN or external traffic.");
    }

    private sealed class Core(Process process, Task<string> errors, Task<string> output) : IAsyncDisposable
    {
        private Task<string> ErrorOutput => errors;
        public static async Task<Core> StartAsync(string executable, JsonObject config, string directory, string name, IPAddress address, int port, CancellationToken token)
        {
            string path = Path.Combine(directory, name + ".json");
            await File.WriteAllTextAsync(path, config.ToJsonString(Configuration.JsonOptions), token);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            bool native = Path.GetFileName(executable) == "WindowsMegaProxy.Dev.Core.exe";
            if (native)
            {
                start.RedirectStandardInput = true;
                start.ArgumentList.Add(executable == NetworkCorePaths.Client(true) ? "experimental" : "stable");
            }
            else foreach (string argument in new[] { "run", "-c", path }) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            var core = new Core(process, process.StandardError.ReadToEndAsync(), process.StandardOutput.ReadToEndAsync());
            try
            {
                if (native)
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(config.ToJsonString());
                    using var writer = new BinaryWriter(process.StandardInput.BaseStream, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(1); writer.Write(data.Length); writer.Write(data); writer.Flush();
                }
                for (int attempt = 0; ; attempt++)
                {
                    if (process.HasExited) throw new IOException(name + ": " + await core.ErrorOutput);
                    try { using var ready = new TcpClient(address.AddressFamily); await ready.ConnectAsync(address, port, token); return core; }
                    catch (SocketException) when (attempt < 60) { await Task.Delay(50, token); }
                }
            }
            catch { await core.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(errors, output);
            }
            finally { process.Dispose(); }
        }
    }
}
