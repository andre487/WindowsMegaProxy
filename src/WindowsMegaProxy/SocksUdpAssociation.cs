using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class SocksUdpAssociation : IDisposable
{
    private readonly TcpClient control;
    public IPEndPoint Relay { get; }
    private SocksUdpAssociation(TcpClient control, IPEndPoint relay) { this.control = control; Relay = relay; }

    public static async Task<SocksUdpAssociation?> OpenAsync(string host, int port, string username, string password, CancellationToken token)
    {
        var control = new TcpClient();
        try
        {
            await control.ConnectAsync(host.Trim('[', ']'), port, token);
            var stream = control.GetStream();
            bool authenticated = username.Length > 0 || password.Length > 0;
            await stream.WriteAsync(new byte[] { 5, 1, authenticated ? (byte)2 : (byte)0 }, token);
            var reply = new byte[2]; await stream.ReadExactlyAsync(reply, token);
            if (reply[0] != 5 || reply[1] != (authenticated ? 2 : 0)) throw new IOException("SOCKS5 authentication method rejected.");
            if (authenticated)
            {
                byte[] user = Encoding.UTF8.GetBytes(username), secret = Encoding.UTF8.GetBytes(password);
                if (user.Length > 255 || secret.Length > 255) throw new IOException("SOCKS5 credentials exceed 255 bytes.");
                using var packet = new MemoryStream();
                packet.WriteByte(1); packet.WriteByte((byte)user.Length); packet.Write(user); packet.WriteByte((byte)secret.Length); packet.Write(secret);
                await stream.WriteAsync(packet.ToArray(), token);
                await stream.ReadExactlyAsync(reply, token);
                if (reply[0] != 1 || reply[1] != 0) throw new IOException("SOCKS5 authentication rejected.");
            }
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            if (header[0] != 5 || header[2] != 0) throw new IOException("Invalid SOCKS5 response.");
            if (header[1] != 0) { control.Dispose(); return null; }
            IPAddress address;
            if (header[3] is 1 or 4)
            {
                var bytes = new byte[header[3] == 1 ? 4 : 16]; await stream.ReadExactlyAsync(bytes, token);
                address = new IPAddress(bytes);
            }
            else if (header[3] == 3)
            {
                var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
                var name = new byte[length[0]]; await stream.ReadExactlyAsync(name, token);
                address = (await Dns.GetHostAddressesAsync(Encoding.ASCII.GetString(name), token))[0];
            }
            else throw new IOException("Invalid SOCKS5 relay address.");
            var relayPort = new byte[2]; await stream.ReadExactlyAsync(relayPort, token);
            if (relayPort[0] == 0 && relayPort[1] == 0) throw new IOException("Invalid SOCKS5 relay port.");
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) address = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
            return new SocksUdpAssociation(control, new IPEndPoint(address, relayPort[0] * 256 + relayPort[1]));
        }
        catch { control.Dispose(); throw; }
    }

    public void Dispose() => control.Dispose();
}
