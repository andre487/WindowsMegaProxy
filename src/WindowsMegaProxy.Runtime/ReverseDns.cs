using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace WindowsMegaProxy;

public sealed class ReverseDns(int cacheHours = 24)
{
    private readonly ConcurrentDictionary<IPAddress, (string? Name, DateTimeOffset Expires)> cache = new();
    public string Resolve(string destination, Func<IPAddress, (string? Name, uint Ttl)>? query = null, DateTimeOffset? now = null)
    {
        int separator = destination.LastIndexOf(':');
        if (separator < 0 || !IPAddress.TryParse(destination[..separator].Trim('[', ']'), out var address)) return destination;
        var time = now ?? DateTimeOffset.UtcNow;
        if (!cache.TryGetValue(address, out var entry) || entry.Expires <= time)
        {
            var result = (query ?? Query)(address);
            entry = (result.Name, time.AddSeconds(cacheHours > 0 ? cacheHours * 3600 : result.Name is null ? 3600 : result.Ttl));
            if (cache.Count >= 4096)
                foreach (var key in cache.Where(pair => pair.Value.Expires <= time).Select(pair => pair.Key).ToArray()) cache.TryRemove(key, out _);
            if (cache.Count < 4096 || cache.ContainsKey(address)) cache[address] = entry;
        }
        return entry.Name is null ? destination : entry.Name + destination[separator..];
    }

    public static string Question(IPAddress address) => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        ? string.Join(".", address.GetAddressBytes().Reverse()) + ".in-addr.arpa"
        : string.Join(".", Convert.ToHexString(address.GetAddressBytes()).ToLowerInvariant().Reverse()) + ".ip6.arpa";

    private static (string? Name, uint Ttl) Query(IPAddress address)
    {
        int status = DnsQuery_W(Question(address), 12, 0, IntPtr.Zero, out var records, IntPtr.Zero);
        try
        {
            if (status != 0) return (null, 0);
            string? name = null;
            uint ttl = uint.MaxValue;
            for (var pointer = records; pointer != IntPtr.Zero;)
            {
                var record = Marshal.PtrToStructure<Record>(pointer);
                if (record.Type is 5 or 12) ttl = Math.Min(ttl, record.Ttl);
                if (record.Type == 12 && record.Length >= IntPtr.Size)
                    name ??= Marshal.PtrToStringUni(record.Data)?.TrimEnd('.');
                pointer = record.Next;
            }
            return string.IsNullOrWhiteSpace(name) ? (null, 0) : (name, ttl);
        }
        finally { if (records != IntPtr.Zero) DnsRecordListFree(records, 1); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Record
    {
        public IntPtr Next, Name;
        public ushort Type, Length;
        public uint Flags, Ttl, Reserved;
        public IntPtr Data;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr servers, out IntPtr records, IntPtr reserved);
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
