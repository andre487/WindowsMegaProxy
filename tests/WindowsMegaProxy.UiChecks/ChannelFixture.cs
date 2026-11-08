using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using WindowsMegaProxy;

internal static class ChannelFixture
{
    public static bool Run(string[] args)
    {
        if (args.Length != 2 || args[0] != "--channel-fixture") return false;
        var app = new Application();
        var window = new Window { Title = "MegaProxy channel test fixture", Width = 300, Height = 100 };
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook(
            (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != ChannelControl.ExitMessage) return IntPtr.Zero;
                handled = true;
                window.Dispatcher.BeginInvoke(window.Close);
                return new IntPtr(0x487);
            });
        window.Loaded += (_, _) => File.WriteAllText(args[1], "ready");
        app.Run(window);
        return true;
    }
}
