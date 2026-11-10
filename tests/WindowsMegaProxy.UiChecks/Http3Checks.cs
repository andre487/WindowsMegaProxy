using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class Http3Checks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(bool network = false)
    {
        CheckPortReservation();
        string directory = Path.GetFullPath("artifacts/http3-checks");
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "failure.txt"));
        File.Delete(Path.Combine(directory, "result.txt"));
        try { await CheckAsync(directory, network); }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); throw; }
    }

    private static async Task CheckAsync(string directory, bool network)
    {
        string alpha = Path.GetFullPath(".tools/sing-box-h3/sing-box.exe");
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", Path.GetFullPath(".tools/sing-box/sing-box.exe"));
        Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_H3_CORE", alpha);
        using var stopping = new CancellationTokenSource();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string certificatePath = Path.Combine(directory, "test-certificate.pem"), keyPath = Path.Combine(directory, "test-key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var probeUri = new Uri($"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/");
        var originTask = ServeOriginAsync(origin, stopping.Token);
        try
        {
            foreach (int serverVersion in new[] { 3, 2, 1 })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(network ? 240 : 180));
                var token = timeout.Token;
                bool h3 = serverVersion == 3;
                var reserved = ReserveProxyPort();
                using var reserve = reserved.Tcp;
                using var udp = reserved.Udp;
                int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
                string configPath = Path.Combine(directory, h3 ? "h3-server.json" : "tcp-server.json");
                var serverConfig = new JsonObject
                {
                    ["log"] = new JsonObject { ["level"] = "warn" },
                    ["inbounds"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "http", ["listen"] = "127.0.0.1", ["listen_port"] = port,
                        ["version"] = h3 ? new JsonArray(1, 3) : new JsonArray(serverVersion),
                        ["users"] = new JsonArray(new JsonObject { ["username"] = "test", ["password"] = "secret" }, new JsonObject { ["username"] = "exit", ["password"] = "exit-secret" }),
                        ["tls"] = new JsonObject { ["enabled"] = true, ["certificate_path"] = certificatePath, ["key_path"] = keyPath }
                    }),
                    ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
                    ["route"] = new JsonObject { ["rules"] = new JsonArray(
                        new JsonObject { ["ip_cidr"] = new JsonArray("127.0.0.1/32"), ["action"] = "route", ["outbound"] = "direct" },
                        new JsonObject { ["action"] = "reject" }) }
                };
                if (network) serverConfig["route"]!["rules"]!.AsArray().Insert(1, new JsonObject
                {
                    ["domain"] = new JsonArray("www.cloudflare.com", "quic.browserleaks.com"), ["action"] = "route", ["outbound"] = "direct"
                });
                File.WriteAllText(configPath, serverConfig.ToJsonString(Configuration.JsonOptions));
                var start = new ProcessStartInfo(alpha) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string argument in new[] { "run", "-c", configPath }) start.ArgumentList.Add(argument);
                reserve.Stop();
                if (h3) udp.Dispose();
                using var server = Process.Start(start)!;
                var errors = server.StandardError.ReadToEndAsync();
                var output = server.StandardOutput.ReadToEndAsync();
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        if (server.HasExited) throw new Exception("Proxy fixture exited: " + await errors);
                        try { using var ready = new TcpClient(); await ready.ConnectAsync(IPAddress.Loopback, port, token); break; }
                        catch (SocketException) when (attempt < 40) { await Task.Delay(50, token); }
                    }
                    var profile = ProxyProfile.Create("HTTP/3 fixture");
                    profile.Proxy["host"] = "127.0.0.1"; profile.Proxy["port"] = port;
                    profile.Proxy["username"] = "test"; profile.Proxy["password"] = "secret";
                    profile.Proxy["allowInvalidProxyCertificate"] = true;
                    profile.Proxy["preferHttp3"] = true;
                    var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy", TlsFingerprint = "chrome" };
                    using var engine = new Engine(directory, probeUri);
                    var messages = new List<string>();
                    engine.Log += message => { lock (messages) messages.Add(message); };
                    try
                    {
                        await engine.StartAsync(settings, true, token);
                        Assert(engine.Http3Selected == h3 && engine.UsedHttp3Fallback == !h3, "Choose HTTP/3 or H2/H1 fallback according to actual QUIC availability.");
                        Assert(engine.AppliedSignature == Configuration.Signature(settings) && engine.AppliedSettings!.ActiveProfile!.PreferHttp3, "Runtime fallback preserves the saved preference and reconnect signature.");
                        using var client = engine.CreateProbeClient(true);
                        string body = await client.GetStringAsync(probeUri, token);
                        Assert(body == "fixture", "Proxy transfers actual target response bytes.");
                        var current = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory, "test-runtime-*.json").Single()))!;
                        Assert(current["inbounds"]!.AsArray().All(item => item!["type"]!.ToString() != "tun"), "The HTTP/3 test must not create TUN.");
                        Assert(h3 ? current["outbounds"]![0]!["version"]!.GetValue<int>() == 3 : current["outbounds"]![0]!["version"]!.GetValue<int>() == 2 && current["outbounds"]![0]!["tls"]!["utls"]!["fingerprint"]!.ToString() == "chrome", "H3 fallback enables H2 and applies the configured TCP fingerprint.");
                        Assert(messages.Any(message => message.Contains("Starting " + Configuration.Http3CoreVersion)), "H3 and its H2/H1 fallback use the alpha core.");
                        if (h3)
                        {
                            await CheckUdpAsync(current, token);
                            if (network)
                            {
                                string status = await engine.CheckHttp3Async(token);
                                File.WriteAllText(Path.Combine(directory, "http3-status.txt"), status);
                                Assert(status.Contains("confirmed"), "Confirm real target HTTP/3 through the configured proxy and SOCKS5 UDP relay.");
                            }
                        }
                        else Assert(await engine.CheckHttp3Async(token) == "", "HTTPS/TCP fallback hides HTTP/3 diagnostics.");
                    }
                    catch { File.WriteAllLines(Path.Combine(directory, "client-log.txt"), messages); throw; }
                    finally { await engine.StopAsync(); }

                    var h2Settings = settings.Copy();
                    h2Settings.ActiveProfile!.Proxy["preferHttp3"] = false;
                    h2Settings.ActiveProfile.Proxy["preferHttp2"] = true;
                    using (var h2Engine = new Engine(directory, probeUri))
                    {
                        try
                        {
                            await h2Engine.StartAsync(h2Settings, true, token);
                            using var client = h2Engine.CreateProbeClient(true);
                            Assert(await client.GetStringAsync(probeUri, token) == "fixture", "H2-only mode transfers bytes through H2 and H1-only servers without QUIC.");
                            Assert(!h2Engine.Http3Selected && !h2Engine.UsedHttp3Fallback, "H2 mode does not probe or select H3.");
                        }
                        finally { await h2Engine.StopAsync(); }
                    }

                    var chain = profile.Copy();
                    chain.Proxy["type"] = "HTTPS_JUMP";
                    chain.Proxy["username"] = "exit"; chain.Proxy["password"] = "exit-secret";
                    chain.Proxy["preferHttp3"] = true;
                    chain.Proxy["jump"] = new JsonObject
                    {
                        ["host"] = "127.0.0.1", ["port"] = port, ["sameAuthentication"] = false,
                        ["username"] = "test", ["password"] = "secret", ["allowInvalidProxyCertificate"] = true
                    };
                    using (var jump = new Engine(directory, probeUri))
                    {
                        try
                        {
                            await jump.StartAsync(new Settings { Profiles = [chain], ActiveProfileId = chain.Id, DefaultAction = "proxy" }, true, token);
                            using var client = jump.CreateProbeClient(true);
                            Assert(await client.GetStringAsync(probeUri, token) == "fixture", "HTTPS Jump transfers actual bytes through both authenticated proxy hops.");
                            Assert(jump.Http3Selected == h3 && jump.UsedHttp3Fallback == !h3, "Both Jump hops use H3 together or fall back together to H2/H1 on alpha.");
                            if (h3)
                            {
                                await CheckUdpAsync(JsonNode.Parse(File.ReadAllText(Directory.GetFiles(directory, "test-runtime-*.json").Single()))!, token);
                                if (network)
                                {
                                    string status = await jump.CheckHttp3Async(token);
                                    File.WriteAllText(Path.Combine(directory, "jump-http3-status.txt"), status);
                                    Assert(status.Contains("confirmed"), "Confirm actual target HTTP/3 through both Jump hops.");
                                }
                            }
                        }
                        finally { await jump.StopAsync(); }
                    }
                    chain.Proxy["preferHttp3"] = false;
                    chain.Proxy["jump"]!["password"] = "incorrect";
                    using (var badJump = new Engine(directory, probeUri))
                    {
                        try
                        {
                            await badJump.StartAsync(new Settings { Profiles = [chain], ActiveProfileId = chain.Id, DefaultAction = "proxy" }, true, token);
                            using var client = badJump.CreateProbeClient(true);
                            bool rejected = false;
                            try { _ = await client.GetStringAsync(probeUri, token); }
                            catch (System.Net.Http.HttpRequestException) { rejected = true; }
                            Assert(rejected, "A failed Jump hop must never be bypassed, even when the exit proxy is directly reachable.");
                        }
                        finally { await badJump.StopAsync(); }
                    }

                    if (h3)
                    {
                        profile.Proxy["type"] = "MASQUE";
                        using (var masque = new Engine(directory, probeUri))
                        {
                            try
                            {
                                await masque.StartAsync(settings, true, token);
                                using var client = masque.CreateProbeClient(true);
                                Assert(await client.GetStringAsync(probeUri, token) == "fixture" && masque.Http3Selected && !masque.UsedHttp3Fallback, "Explicit MASQUE sends real proxy traffic without fallback.");
                            }
                            finally { await masque.StopAsync(); }
                        }
                        profile.Proxy["type"] = "HTTPS";
                        profile.Proxy["allowInvalidProxyCertificate"] = false;
                        using var untrusted = new Engine(directory, probeUri);
                        bool failed = false;
                        try { await untrusted.StartAsync(settings, true, token); }
                        catch (System.Net.Http.HttpRequestException) { failed = true; }
                        Assert(failed && !untrusted.Running && !untrusted.UsedHttp3Fallback, "Untrusted certificates never cause TCP fallback.");
                        profile.Proxy["allowInvalidProxyCertificate"] = true;
                        profile.Proxy["password"] = "wrong";
                        using var unauthenticated = new Engine(directory, probeUri);
                        failed = false;
                        try { await unauthenticated.StartAsync(settings, true, token); }
                        catch (System.Net.Http.HttpRequestException) { failed = true; }
                        Assert(failed && !unauthenticated.Running && !unauthenticated.UsedHttp3Fallback, "Rejected credentials never cause TCP fallback.");
                    }
                }
                finally
                {
                    if (!server.HasExited) server.Kill(true);
                    await server.WaitForExitAsync();
                    File.WriteAllText(Path.Combine(directory, h3 ? "h3-server.log" : "tcp-server.log"), await errors + await output);
                }
            }
            using var socksTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CheckSocksWithoutUdpAsync(directory, socksTimeout.Token);
            File.WriteAllText(Path.Combine(directory, "result.txt"), "Real HTTP/3 CONNECT and CONNECT-UDP, MASQUE, HTTPS Jump over TCP/H3 with separate credentials and mandatory first hop, H2 and H1 fallback, hidden H3 checks on TCP/SOCKS5 without UDP, certificate/authentication rejection and cleanup passed. No TUN." + (network ? " External target HTTP/3 through the proxy confirmed." : " No external target traffic."));
        }
        finally
        {
            stopping.Cancel(); origin.Stop(); await originTask;
            File.Delete(keyPath); File.Delete(certificatePath);
        }
    }

    internal static (TcpListener Tcp, UdpClient Udp) ReserveProxyPort(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        for (int attempt = 0; ; attempt++)
        {
            var tcp = new TcpListener(address, 0) { ExclusiveAddressUse = true };
            var udp = new UdpClient(address.AddressFamily) { ExclusiveAddressUse = true };
            try
            {
                tcp.Start();
                // Windows UDP exclusions differ from TCP; the fixture needs both on the same port.
                udp.Client.Bind(tcp.LocalEndpoint);
                return (tcp, udp);
            }
            catch (SocketException error) when (attempt < 31 && error.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse)
            { tcp.Stop(); udp.Dispose(); }
            catch { tcp.Stop(); udp.Dispose(); throw; }
        }
    }

    private static void CheckPortReservation()
    {
        var reserved = ReserveProxyPort();
        using var tcp = reserved.Tcp;
        using var udp = reserved.Udp;
        Assert(tcp.LocalEndpoint.Equals(udp.Client.LocalEndPoint), "Proxy fixtures reserve the same port for TCP and UDP.");
        using var contender = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
        bool rejected = false;
        try { contender.Client.Bind(tcp.LocalEndpoint); }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { rejected = true; }
        Assert(rejected, "The fallback UDP blackhole remains exclusively reserved until fixture cleanup.");
    }

    private static async Task CheckSocksWithoutUdpAsync(string directory, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        async Task RejectUdpAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var stream = peer.GetStream();
            var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, token);
            await stream.WriteAsync(new byte[] { 5, 0 }, token);
            var command = new byte[10]; await stream.ReadExactlyAsync(command, token);
            Assert(command[1] == 3, "Probe UDP ASSOCIATE rather than guessing UDP support from the SOCKS5 profile type.");
            await stream.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
        }
        var rejection = RejectUdpAsync();
        var profile = ProxyProfile.Create("TCP-only SOCKS5 fixture");
        profile.Proxy["type"] = "SOCKS5"; profile.Proxy["host"] = "127.0.0.1";
        profile.Proxy["port"] = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var engine = new Engine(directory);
        try
        {
            await engine.StartAsync(new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" }, true, token);
            Assert(await engine.CheckHttp3Async(token) == "" && !engine.Http3Selected, "Hide HTTP/3 diagnostics when the SOCKS5 server rejects UDP.");
            await rejection;
        }
        finally { await engine.StopAsync(); }
    }

    internal static async Task CheckUdpAsync(JsonNode config, CancellationToken token, IPAddress? destination = null, bool allowed = true)
    {
        destination ??= IPAddress.Loopback;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var echo = new UdpClient(new IPEndPoint(destination, 0));
        var inbound = config["inbounds"]![0]!;
        using var association = await SocksUdpAssociation.OpenAsync("127.0.0.1", inbound["listen_port"]!.GetValue<int>(),
            "diagnostic", inbound["users"]![0]!["password"]!.ToString(), deadline.Token);
        Assert(association is not null, "Local SOCKS5 diagnostic relay supports UDP ASSOCIATE.");
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        byte[] payload = Encoding.ASCII.GetBytes("udp-fixture");
        byte[] packet = [0, 0, 0, (byte)(destination.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1),
            .. destination.GetAddressBytes(), (byte)(port >> 8), (byte)port, .. payload];
        await client.SendAsync(packet, association!.Relay, deadline.Token);
        if (!allowed)
        {
            using var rejected = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            rejected.CancelAfter(TimeSpan.FromSeconds(1));
            try { await echo.ReceiveAsync(rejected.Token); throw new Exception("Blocked IPv6 UDP reached its destination."); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { return; }
        }
        var request = await echo.ReceiveAsync(deadline.Token);
        Assert(request.Buffer.SequenceEqual(payload), "CONNECT-UDP delivers the original datagram.");
        await echo.SendAsync(request.Buffer, request.RemoteEndPoint, deadline.Token);
        var response = await client.ReceiveAsync(deadline.Token);
        Assert(response.Buffer.AsSpan(^payload.Length).SequenceEqual(payload), "CONNECT-UDP returns the response datagram.");
    }

    private static async Task ServeOriginAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? first = await reader.ReadLineAsync(token);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
                string body = first?.StartsWith("HEAD ", StringComparison.Ordinal) == true ? "" : "fixture";
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\n" + body), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
