using System;
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
    public DiagnosticLog(string directory) => this.directory = directory;

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

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {Redact(message)}{Environment.NewLine}";
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
}
