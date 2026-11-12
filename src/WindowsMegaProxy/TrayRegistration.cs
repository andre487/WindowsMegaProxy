using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsMegaProxy;

internal static class TrayRegistration
{
    // NotifyIcon exposes neither its shell ID nor its HWND. Keep this pinned-runtime bridge covered by UI tests.
    private static readonly FieldInfo Id = typeof(NotifyIcon).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NotifyIcon).FullName, "_id");
    private static readonly FieldInfo Window = typeof(NotifyIcon).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(NotifyIcon).FullName, "_window");

    internal static bool Exists(NotifyIcon icon)
    {
        if (Window.GetValue(icon) is not NativeWindow window || window.Handle == IntPtr.Zero) return false;
        var identifier = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Window = window.Handle, Id = Convert.ToUInt32(Id.GetValue(icon)) };
        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref Identifier identifier, out Rect rectangle);
}
