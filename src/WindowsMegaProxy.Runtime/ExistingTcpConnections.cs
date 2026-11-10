using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace WindowsMegaProxy;

public static class ExistingTcpConnections
{
    [StructLayout(LayoutKind.Sequential)]
    public record struct Row(uint State, uint LocalAddress, uint LocalPort, uint RemoteAddress, uint RemotePort, uint Pid);
    public sealed record Candidate(Row Connection, long ProcessStart);

    public static bool ShouldClose(Settings settings, string path, IPAddress remote)
    {
        if (!settings.ResetExistingTcpConnections || IPAddress.IsLoopback(remote) ||
            Configuration.DirectProcessNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return false;
        var bytes = remote.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] == 0 || bytes[0] >= 224 ||
            settings.BypassLan && (bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31 || bytes[0] == 192 && bytes[1] == 168)) return false;
        foreach (var rule in settings.Rules.Where(rule => rule.Enabled))
        {
            bool matches = rule.Source switch
            {
                "all" => true,
                "exe" => path.Equals(Path.GetFullPath(rule.Path), StringComparison.OrdinalIgnoreCase),
                "folder" => path.StartsWith(Path.GetFullPath(rule.Path).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!matches) continue;
            string destination = rule.Destination.Trim();
            if (destination.Length > 0)
            {
                if (IPAddress.TryParse(destination, out var ip)) { if (!ip.Equals(remote)) continue; }
                else if (IPNetwork.TryParse(destination, out var network)) { if (!network.Contains(remote)) continue; }
                else return false; // Old sockets have no reliable domain/Podkop metadata.
            }
            return rule.Source != "all" && rule.Action == "proxy";
        }
        return false;
    }

    public static Candidate[] Capture(Settings settings, Action<string> log)
    {
        try
        {
            var candidates = new List<Candidate>();
            foreach (var group in Read().Where(row => row.State == 5 && row.RemotePort != 0x3500 && row.Pid > 4 && row.Pid != Environment.ProcessId).GroupBy(row => row.Pid))
            {
                try
                {
                    using var process = Process.GetProcessById((int)group.Key);
                    string? path = process.MainModule?.FileName;
                    if (path is null || path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    long start = process.StartTime.ToUniversalTime().Ticks;
                    foreach (var row in group)
                        if (ShouldClose(settings, path, new IPAddress(row.RemoteAddress))) candidates.Add(new(row, start));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException) { }
            }
            log($"Existing TCP reset: captured {candidates.Count} eligible IPv4 connections.");
            return candidates.ToArray();
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        { log("Existing TCP capture failed: " + error.Message); return []; }
    }

    public static void Close(Candidate[] candidates, Action<string> log)
    {
        if (candidates.Length == 0) return;
        try
        {
            var current = Read().ToHashSet();
            int closed = 0;
            foreach (var candidate in candidates)
            {
                var row = candidate.Connection;
                if (!current.Contains(row)) continue;
                try
                {
                    using var process = Process.GetProcessById((int)row.Pid);
                    if (process.StartTime.ToUniversalTime().Ticks != candidate.ProcessStart) continue;
                    row.State = 12;
                    uint result = SetTcpEntry(ref row);
                    if (result == 0) closed++;
                    else log($"Existing TCP reset failed: PID={row.Pid}; Win32={result}.");
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException)
                { log("Existing TCP reset skipped: " + error.Message); }
            }
            log($"Existing TCP reset: closed {closed} IPv4 connections after core startup.");
        }
        catch (Exception error) when (error is Win32Exception or IOException) { log("Existing TCP reset failed: " + error.Message); }
    }

    public static Row[] Read()
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (result != 122) throw new Win32Exception((int)result);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                int count = Marshal.ReadInt32(buffer);
                var rows = new Row[count];
                for (int i = 0; i < count; i++) rows[i] = Marshal.PtrToStructure<Row>(buffer + 4 + i * Marshal.SizeOf<Row>());
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("The TCP table kept changing during enumeration.");
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint SetTcpEntry(ref Row row);
}
