using System;
using System.IO;
using System.Text;

namespace WindowsMegaProxy;

internal static class NativeCore
{
    public static bool IsHost(string executable) => Path.GetFileName(executable) is "WindowsMegaProxy.Core.exe" or "WindowsMegaProxy.Dev.Core.exe";

    public static byte[] Request(int operation, string configuration, string? url = null)
    {
        byte[] config = Encoding.UTF8.GetBytes(configuration);
        if (operation is < 0 or > 2 || config.Length is 0 or > 16 * 1024 * 1024 || configuration.Contains('\0'))
            throw new InvalidDataException("Invalid network core request.");
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(operation);
        writer.Write(config.Length);
        writer.Write(config);
        if (operation == 2)
        {
            byte[] address = Encoding.UTF8.GetBytes(url ?? "");
            if (address.Length is 0 or > 8192 || url!.Contains('\0')) throw new InvalidDataException("Invalid network probe URL.");
            writer.Write(address.Length);
            writer.Write(address);
        }
        return buffer.ToArray();
    }
}
