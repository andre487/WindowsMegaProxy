using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal sealed class SetupLogReader : IDisposable
{
    private readonly Dictionary<string, StreamReader> readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> errors = new(StringComparer.OrdinalIgnoreCase);
    internal string Read(string path)
    {
        try
        {
            if (!readers.TryGetValue(path, out var reader))
            {
                reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, true);
                readers.Add(path, reader);
            }
            var buffer = new char[16384];
            int count = reader.Read(buffer, 0, buffer.Length);
            return count == 0 ? "" : "[" + Path.GetFileName(path) + "]\r\n" + new string(buffer, 0, count);
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return errors.Add(path) ? "Cannot read setup log: " + path + ": " + error.Message + "\r\n" : "";
        }
    }
    public void Dispose() { foreach (var reader in readers.Values) reader.Dispose(); }
}
