using System;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private readonly System.Windows.Threading.DispatcherTimer trayTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool trayMissing;

    private void CheckTrayIcon()
    {
        if (quitting) return;
        try { CheckTrayRegistration(); }
        catch (Exception error)
        {
            // A runtime compatibility issue must not take down the client or its active connections.
            trayTimer.Stop();
            WriteLog("Tray registration monitoring disabled: " + error);
        }
    }

    private void CheckTrayRegistration()
    {
        if (!TrayRegistration.Exists(tray))
        {
            if (!trayMissing) WriteLog("Tray icon is absent from Explorer; retrying registration.");
            trayMissing = true;
            tray.Visible = false;
            tray.Visible = true;
        }
        if (trayMissing && TrayRegistration.Exists(tray))
        {
            trayMissing = false;
            WriteLog("Tray icon registration confirmed by Explorer.");
        }
    }

    private void RestoreTrayIcon()
    {
        if (quitting) return;
        // NotifyIcon remembers a failed initial NIM_ADD as successful; force a fresh add.
        tray.Visible = false;
        tray.Visible = true;
        CheckTrayIcon();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

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
