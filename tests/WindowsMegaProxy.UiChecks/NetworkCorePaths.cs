using System.IO;
using System.Text;
using System.Text.Json.Nodes;

internal static class NetworkCorePaths
{
    public static string Client(bool experimental = false) => Path.GetFullPath(
        $".tools/dev-cores/{(experimental ? "sing-box-h3" : "sing-box")}/WindowsMegaProxy.Dev.Core.exe");

    public static string FixtureHost()
    {
        string path = Path.Combine(System.AppContext.BaseDirectory, "WindowsMegaProxy.Dev.Core.exe");
        File.Copy(System.Environment.ProcessPath!, path, overwrite: true);
        return path;
    }

    public static byte[] Request(int operation, JsonNode config)
    {
        byte[] data = Encoding.UTF8.GetBytes(config.ToJsonString());
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(operation); writer.Write(data.Length); writer.Write(data);
        return buffer.ToArray();
    }

    public static (int Operation, JsonNode Config) ReadRequest()
    {
        using var reader = new BinaryReader(System.Console.OpenStandardInput(), Encoding.UTF8, leaveOpen: true);
        int operation = reader.ReadInt32(), length = reader.ReadInt32();
        if (length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid fixture request.");
        return (operation, JsonNode.Parse(reader.ReadBytes(length))!);
    }
}
