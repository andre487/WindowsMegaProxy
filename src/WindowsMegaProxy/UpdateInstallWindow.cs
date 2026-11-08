using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public sealed class UpdateInstallWindow : Window
{
    public string? Action { get; private set; }

    public UpdateInstallWindow(bool service)
    {
        Title = Ui.T("Install update");
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Ui.T("When should the update be installed?"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        void Add(string action, string title, string? description = null)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = Ui.T(title), TextWrapping = TextWrapping.Wrap });
            if (description is not null)
            {
                var detail = new TextBlock { Text = Ui.T(description), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                content.Children.Add(detail);
            }
            var button = new Button { Name = action == "now" ? "UpdateNowButton" : "Update" + action + "Button", Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12) };
            System.Windows.Automation.AutomationProperties.SetName(button, Ui.T(title));
            button.Click += (_, _) => { Action = action; DialogResult = true; };
            panel.Children.Add(button);
        }
        Add("now", "Update now", service ? "While the service is updating, connections will use the system network settings." : null);
        if (service)
        {
            Add("restart", "Restart computer and update");
            Add("reboot", "Update at the next computer restart");
        }
        else Add("launch", "Update at the next application start");
        panel.Children.Add(new Button { Content = Ui.T("Cancel"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) });
        Content = panel;
    }
}
