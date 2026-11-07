using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WindowsMegaProxy;

public sealed record ClientAccess(string Sid, bool CanEdit)
{
    public static bool MayEdit(bool administrator, bool system, bool restricted, bool lowIntegrity, bool appContainer, bool network) =>
        (administrator || system) && !restricted && !lowIntegrity && !appContainer && !network;

    public static ClientAccess Capture(NamedPipeServerStream pipe)
    {
        ClientAccess? result = null;
        pipe.RunAsClient(() => result = Current());
        return result ?? throw new UnauthorizedAccessException("Could not authenticate the service client.");
    }

    public static ClientAccess Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsAuthenticated || identity.User is null || identity.IsAnonymous)
            throw new UnauthorizedAccessException("An authenticated Windows account is required.");
        bool network = identity.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) == true;
        if (network) throw new UnauthorizedAccessException("Remote service clients are not allowed.");
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        bool administrator = new WindowsPrincipal(identity).IsInRole(administrators);
        IntPtr token = identity.AccessToken.DangerousGetHandle();
        // UAC's filtered administrator token deliberately keeps the administrator SID as deny-only.
        if (!administrator && ReadInt(token, 18) == 3) administrator = HasDenyOnlyAdministrator(token, administrators);
        IntPtr integrity = Read(token, 25);
        bool low;
        try { low = int.Parse(new SecurityIdentifier(Marshal.ReadIntPtr(integrity)).Value.Split('-')[^1]) < 0x2000; }
        finally { Marshal.FreeHGlobal(integrity); }
        bool system = identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
        return new(identity.User.Value, MayEdit(administrator, system, IsTokenRestricted(token), low, ReadInt(token, 29) != 0, network));
    }

    private static bool HasDenyOnlyAdministrator(IntPtr token, SecurityIdentifier administrators)
    {
        IntPtr groups = Read(token, 2);
        try
        {
            int count = Marshal.ReadInt32(groups);
            int offset = (int)Marshal.OffsetOf<Groups>(nameof(Groups.First));
            for (int i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidAttributes>(groups + offset + i * Marshal.SizeOf<SidAttributes>());
                if (new SecurityIdentifier(group.Sid) == administrators && (group.Attributes & 0x10) != 0) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(groups); }
    }
    private static int ReadInt(IntPtr token, int type)
    {
        IntPtr value = Read(token, type);
        try { return Marshal.ReadInt32(value); }
        finally { Marshal.FreeHGlobal(value); }
    }
    private static IntPtr Read(IntPtr token, int type)
    {
        GetTokenInformation(token, type, IntPtr.Zero, 0, out int length);
        if (length <= 0 || length > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr data = Marshal.AllocHGlobal(length);
        if (GetTokenInformation(token, type, data, length, out _)) return data;
        Marshal.FreeHGlobal(data);
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Groups { public uint Count; public SidAttributes First; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr information, int length, out int required);
    [DllImport("advapi32.dll")] private static extern bool IsTokenRestricted(IntPtr token);
}
