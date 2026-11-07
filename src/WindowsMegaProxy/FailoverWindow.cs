using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public sealed class FailoverWindow : Window
{
    public FailoverWindow(Settings settings, Func<string, string[], Task> save)
    {
        Title = Ui.T("Failover"); Width = 500; Height = 510; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        string[] modes = ["DISABLED", "SELECTED", "ALL"];
        var mode = new ComboBox { ItemsSource = new[] { Ui.T("Disabled"), Ui.T("Selected profiles"), Ui.T("All profiles") }, SelectedIndex = Array.IndexOf(modes, FailoverPolicy.Mode(settings)) };
        panel.Children.Add(new Label { Content = Ui.T("Failover mode"), Target = mode, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = Ui.T("After repeated proxy transport failures, retry the current profile and then use backups in profile order. Exit IP and country may change. Certificate and authentication errors never disable verification."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        var boxes = settings.Profiles.Where(profile => profile.Supported).Select(profile => (profile.Id, Box: new CheckBox
        { Content = profile.SelectionLabel, IsChecked = FailoverPolicy.Selected(settings).Contains(profile.Id), Margin = new Thickness(0, 5, 0, 5) })).ToArray();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        void Enable() { foreach (var item in boxes) item.Box.IsEnabled = mode.SelectedIndex == 1; }
        async Task Save()
        {
            try { await save(modes[mode.SelectedIndex], boxes.Where(item => item.Box.IsChecked == true).Select(item => item.Id).ToArray()); status.Text = ""; }
            catch (Exception) { status.Text = Ui.T("Could not save failover settings."); }
        }
        foreach (var item in boxes) { panel.Children.Add(item.Box); item.Box.Click += async (_, _) => await Save(); }
        panel.Children.Add(status);
        Enable();
        mode.SelectionChanged += async (_, _) => { Enable(); await Save(); };
    }
}
