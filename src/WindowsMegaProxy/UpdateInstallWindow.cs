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
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 14; FontWeight = FontWeights.Normal;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Ui.T("Choose an update option"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        void Add(string action, string title, string? description = null)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = Ui.T(title), TextWrapping = TextWrapping.Wrap });
            if (description is not null)
            {
                var detail = new TextBlock { Text = Ui.T(description), Style = UiTheme.ExplanationStyle, Margin = new Thickness(0, 4, 0, 0) };
                content.Children.Add(detail);
            }
            var button = new Button { Name = action == "now" ? "UpdateNowButton" : "Update" + action + "Button", Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12) };
            System.Windows.Automation.AutomationProperties.SetName(button, Ui.T(title));
            button.Click += (_, _) => { Action = action; DialogResult = true; };
            panel.Children.Add(button);
        }
        Add("now", service ? "Now without restarting" : "Now", "During the update, connections will use the system route.");
        if (service)
        {
            Add("reboot", "At the next computer startup", "The current version will keep running until the next computer startup.");
            Add("restart", "Restart now", "The computer will restart and the update will be installed before user sign-in.");
        }
        else Add("launch", "At the next application start", "The current version will keep running until the next application start.");
        panel.Children.Add(new Button { Content = Ui.T("Cancel"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) });
        Content = panel;
    }
}
