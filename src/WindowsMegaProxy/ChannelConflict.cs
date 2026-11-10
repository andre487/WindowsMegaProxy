using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public static class ChannelConflict
{
    private static readonly SemaphoreSlim gate = new(1);

    public static Window CreateDialog(Window? owner = null)
    {
        var dialog = new Window
        {
            Title = Ui.T("Another MegaProxy version is running"), Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner, FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"), FontSize = 14
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Ui.F("{0} is already running. Choose which version should remain active.", ChannelControl.OtherDisplayName), TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var stop = new Button { Content = Ui.F("Stop {0}", ChannelControl.OtherDisplayName), Margin = new Thickness(0, 0, 8, 0) };
        var exit = new Button { Content = Ui.F("Exit {0}", AppIdentity.DisplayName), IsCancel = true, IsDefault = true };
        stop.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(stop); buttons.Children.Add(exit); panel.Children.Add(buttons); dialog.Content = panel;
        return dialog;
    }

    public static async Task<bool> ResolveAsync(Window? owner = null)
    {
        await gate.WaitAsync();
        try
        {
            if (!await Task.Run(ChannelControl.OtherRunning)) return true;
            var dialog = CreateDialog(owner);
            bool stopOther = dialog.ShowDialog() == true;
            if (owner is not null && !owner.IsLoaded) return false;
            if (!stopOther && !await Task.Run(ChannelControl.CurrentServiceRunning)) return false;
            if (StartupConfiguration.IsElevated) await ChannelControl.ResolveAsync(stopOther);
            else
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--resolve-channel-conflict");
                start.ArgumentList.Add(stopOther ? "stop-other" : "exit-current");
                using var helper = Process.Start(start) ?? throw new IOException("Could not start the channel switch helper.");
                await helper.WaitForExitAsync();
                if (helper.ExitCode != 0) throw new IOException("Could not stop the selected MegaProxy host. See the local log and retry.");
            }
            if (stopOther && await Task.Run(ChannelControl.OtherRunning)) throw new IOException("The other MegaProxy host is still running.");
            return stopOther;
        }
        finally { gate.Release(); }
    }
}
