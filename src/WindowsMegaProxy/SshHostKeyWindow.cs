using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public sealed class SshHostKeyWindow : Window
{
    public SshHostKeyWindow(SshHostKeyUpdate update)
    {
        Title = Ui.T("SSH server key changed");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = update.ProfileName + " · " + update.Host + ":" + update.Port, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = Ui.T("The server key differs from the saved key. The server may have been reinstalled, or the connection may be intercepted. Verify the new fingerprint before trusting it."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16)
        });
        AddFingerprint(panel, Ui.T("Saved fingerprint"), update.PreviousFingerprint);
        AddFingerprint(panel, Ui.T("New fingerprint"), update.Fingerprint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var trust = new Button { Content = Ui.T("Trust new key"), Margin = new Thickness(0, 0, 8, 0) };
        trust.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(trust);
        buttons.Children.Add(new Button { Content = Ui.T("Cancel connection"), IsCancel = true, IsDefault = true });
        panel.Children.Add(buttons);
    }

    private static void AddFingerprint(Panel panel, string label, string value)
    {
        var box = new TextBox { Text = value, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new Label { Content = label, Target = box, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(box);
    }
}
