using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class AuthenticationChecks
{
    public static async Task RunAsync()
    {
        foreach (string invalid in new[] { "https://example.com", "example.com:443", "user@example.com", "example.com/path", "a\r\nb" })
        {
            try { ProxyAuthenticationProbe.ValidateHost(invalid); throw new Exception("Invalid knock host accepted."); }
            catch (ArgumentException) { }
        }
        foreach (string valid in new[] { "", "knock.example.com", "127.0.0.1", "2001:db8::1", "[::1]" }) ProxyAuthenticationProbe.ValidateHost(valid);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        foreach (int status in new[] { 407, 200, 404 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var profile = ProxyProfile.Create("Authentication fixture");
            profile.Proxy["host"] = "127.0.0.1";
            profile.Proxy["port"] = ((IPEndPoint)listener.LocalEndpoint).Port;
            profile.Proxy["username"] = "test";
            profile.Proxy["password"] = "secret";
            profile.Proxy["authProbeHost"] = "knock.example.com";
            profile.Proxy["allowInvalidProxyCertificate"] = true;
            async Task ServeAsync()
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(certificate);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                string? first = await reader.ReadLineAsync(timeout.Token);
                bool authorization = false;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token)))
                    authorization |= line == "Proxy-Authorization: Basic dGVzdDpzZWNyZXQ=";
                if (first != "CONNECT knock.example.com:443 HTTP/1.1" || !authorization) throw new Exception("Knock request must authenticate preemptively.");
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\n\r\n"), timeout.Token);
            }
            var server = ServeAsync();
            bool rejected = false;
            await ProxyAuthenticationProbe.CheckAsync(profile, _ => { }, timeout.Token).ContinueWith(task =>
            {
                if (task.Exception?.InnerException is InvalidOperationException error && error.Message.Contains("407")) rejected = true;
                else task.GetAwaiter().GetResult();
            });
            await server;
            if (rejected != (status == 407)) throw new Exception("Only an explicit 407 proves authentication rejection.");
            var restored = PortableConfiguration.Import(PortableConfiguration.Export(new Settings { Profiles = [profile], ActiveProfileId = profile.Id }, true).ToJsonString(), new Settings());
            if (restored.ActiveProfile!.Proxy["authProbeHost"]!.ToString() != "knock.example.com") throw new Exception("Preserve the shared knock host field.");
        }
    }
}
