using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class ProxyAuthenticationProbe
{
    public static void ValidateHost(string host)
    {
        if (host.Length == 0) return;
        bool ip = System.Net.IPAddress.TryParse(host, out _);
        bool dns = Uri.CheckHostName(host) == UriHostNameType.Dns && !host.Contains(':');
        if (host.Length > 253 || !ip && !dns || Array.Exists(host.ToCharArray(), char.IsWhiteSpace))
            throw new ArgumentException("Enter a host without a URL, path or port.");
    }

    public static async Task CheckAsync(ProxyProfile? profile, Action<string> log, CancellationToken token)
    {
        if (profile?.Type != "HTTPS") return;
        string host = profile.Proxy["authProbeHost"]?.GetValue<string>() ?? "";
        ValidateHost(host);
        if (host.Length == 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        int? status = null;
        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(profile.Host.Trim('[', ']'), profile.Port, timeout.Token);
            bool insecure = profile.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false;
            using var tls = insecure ? new SslStream(socket.GetStream(), false, (_, _, _, _) => true) : new SslStream(socket.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = profile.Host.Trim('[', ']') }, timeout.Token);
            string authority = host.Contains(':') ? $"[{host.Trim('[', ']')}]:443" : host + ":443";
            string user = profile.Proxy["username"]?.GetValue<string>() ?? "";
            string password = profile.Proxy["password"]?.GetValue<string>() ?? "";
            string authorization = user.Length == 0 && password.Length == 0 ? "" : "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)) + "\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{authorization}\r\n"), timeout.Token);
            var line = new StringBuilder();
            var one = new byte[1];
            while (line.Length < 4096 && await tls.ReadAsync(one, timeout.Token) != 0)
            {
                if (one[0] == '\n') break;
                line.Append((char)one[0]);
            }
            var parts = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) && int.TryParse(parts[1], out int code)) status = code;
        }
        catch (Exception error) when ((error is IOException or SocketException || error is OperationCanceledException && !token.IsCancellationRequested))
        {
            log("Knock host authentication check was inconclusive.");
            return;
        }
        // Masked servers may return success for rejected credentials; only 407 proves rejection.
        if (status == 407) throw new InvalidOperationException("The proxy rejected authentication (HTTP 407).");
        log("Knock host authentication check completed; credentials were not rejected explicitly.");
    }
}
