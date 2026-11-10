using System;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private static void PositionTrayMenu(IntPtr handle)
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        const uint flags = 0x0001 | 0x0004 | 0x0010; // Keep size, z-order and activation.
        // NotifyIcon clicks do not update WPF's cached mouse position. Use screen pixels.
        SetWindowPos(handle, IntPtr.Zero, cursor.X, cursor.Y, 0, 0, flags);
        // Moving first lets the popup adopt the destination monitor's DPI before fitting it.
        if (!GetWindowRect(handle, out var bounds)) return;
        int x = Math.Clamp(cursor.X, area.Left, Math.Max(area.Left, area.Right - (bounds.Right - bounds.Left)));
        int y = Math.Clamp(cursor.Y, area.Top, Math.Max(area.Top, area.Bottom - (bounds.Bottom - bounds.Top)));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayMenuRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out TrayMenuRect bounds);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
