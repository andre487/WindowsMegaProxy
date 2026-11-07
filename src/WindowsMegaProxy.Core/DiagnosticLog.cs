using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsMegaProxy;

public sealed class DiagnosticLog
{
    private readonly object gate = new();
    private readonly string directory;
    private long limit = 5 * 1024 * 1024;
    public event Action? Changed;
    public DiagnosticLog(string directory, int initialLimit = 5)
    {
        if (initialLimit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(initialLimit));
        this.directory = directory;
        limit = initialLimit * 1024L * 1024;
    }

    public void SetLimit(int megabytes)
    {
        if (megabytes is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(megabytes));
        lock (gate)
        {
            limit = megabytes * 1024L * 1024;
            Directory.CreateDirectory(directory);
            Rotate(0);
        }
    }

    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"\x1B\[[0-9;]*m", "");
        text = Regex.Replace(text, @"(?i)\b(?:https?|socks5?)://[^\s]+", "[URL]");
        text = Regex.Replace(text, @"(?i)(authorization\s*[:=]\s*)(?:Bearer|Basic)\s+\S+", "$1[redacted]");
        text = Regex.Replace(text, "(?i)(\"?(?:password|privateKey|authorization|secret|token)\"?\\s*[:=]\\s*)(?:\"(?:\\\\.|[^\"\\\\])*\"|[^\\s,;]+)", "$1[redacted]");
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal)) return "[private key redacted]";
        return text.Length > 4096 ? text[..4096] : text;
    }

    public void Write(string message) => WriteBatch([message]);

    internal void WriteBatch(IEnumerable<string> messages)
    {
        var line = string.Concat(messages.Select(message => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {Redact(message)}{Environment.NewLine}"));
        if (line.Length == 0) return;
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            Rotate(Encoding.UTF8.GetByteCount(line));
            File.AppendAllText(Path.Combine(directory, "current.log"), line, new UTF8Encoding(false));
        }
        Changed?.Invoke();
    }

    // Two equally sized files bound the total disk budget, including after the limit is reduced.
    private void Rotate(int incoming)
    {
        string current = Path.Combine(directory, "current.log"), previous = Path.Combine(directory, "previous.log");
        if (File.Exists(previous) && new FileInfo(previous).Length > limit / 2) File.Delete(previous);
        if (File.Exists(current) && new FileInfo(current).Length + incoming > limit / 2)
        {
            if (new FileInfo(current).Length <= limit / 2) File.Move(current, previous, true);
            else File.Delete(current);
        }
    }

    public string Read(bool tailOnly = false)
    {
        lock (gate)
        {
            var text = string.Concat(new[] { "previous.log", "current.log" }.Select(name =>
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) return "";
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (tailOnly) stream.Seek(Math.Max(0, stream.Length - 64000), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }));
            return tailOnly && text.Length > 64000 ? text[^64000..] : text;
        }
    }

    public void Export(string destination)
    {
        var inputs = new List<(FileStream Stream, long Length)>();
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            lock (gate)
            {
                foreach (string name in new[] { "previous.log", "current.log" })
                {
                    string path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    inputs.Add((input, input.Length));
                }
            }
            using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                foreach (var input in inputs)
                {
                    long remaining = input.Length;
                    while (remaining > 0)
                    {
                        int count = input.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count == 0) throw new EndOfStreamException("The log snapshot was truncated during export.");
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                }
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            foreach (var input in inputs) input.Stream.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
