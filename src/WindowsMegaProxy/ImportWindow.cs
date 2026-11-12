using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public sealed class ImportWindow : Window
{
    private readonly List<(string Key, CheckBox Box)> options = [];
    private readonly List<(string Id, CheckBox Box)> missing = [];
    private readonly CheckBox globals = new() { Content = Ui.T("Global settings and active profile"), Margin = new Thickness(0, 8, 0, 8) };
    public HashSet<string> Selected => options.Where(item => item.Box.IsChecked == true).Select(item => item.Key).ToHashSet();
    public HashSet<string> RemoveIds => missing.Where(item => item.Box.IsChecked == true).Select(item => item.Id).ToHashSet();
    public bool ApplyGlobals => globals.IsChecked == true;

    public ImportWindow(ImportReview review)
    {
        Title = Ui.T("Import options"); Width = 540; MinWidth = 480; Height = 600; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"); FontSize = 14;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var apply = new Button { Content = Ui.T("Import"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = Ui.T("Cancel"), IsCancel = true });
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = Ui.F("Import {0} profiles. Unsupported entries skipped: {1}.", review.Import.Profiles.Count, review.Import.Skipped), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Ui.T("Choose which existing options to replace. Unchecked options and missing profiles are kept."), Style = UiTheme.ExplanationStyle, Margin = new Thickness(0, 8, 0, 8) });
        if (review.UnsupportedOptions.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = Ui.T("Import notices") + "\n" +
                    string.Join("\n", review.UnsupportedOptions.Select(option => "• " + Ui.T(option))),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
            });
        foreach (var group in review.Options.GroupBy(option => option.ProfileId))
        {
            panel.Children.Add(new TextBlock { Text = group.First().ProfileName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var option in group)
            {
                var box = new CheckBox { Content = Ui.T(option.Group), Margin = new Thickness(0, 4, 0, 4) };
                options.Add((option.ProfileId + ":" + option.Group, box)); panel.Children.Add(box);
            }
        }
        if (review.HasGlobalOptions) panel.Children.Add(globals);
        if (review.Import.Document.ContainsKey("subscription")) panel.Children.Add(new TextBlock
        {
            Text = Ui.T("This file changes the configuration subscription. Its sources are trusted to update imported profiles and settings."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8)
        });
        if (review.Missing.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = Ui.T("Remove missing profiles (optional)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
            foreach (var profile in review.Missing)
            {
                var box = new CheckBox { Content = profile.Name, Margin = new Thickness(0, 4, 0, 4) };
                missing.Add((profile.Id, box)); panel.Children.Add(box);
            }
        }
        if (review.UnsafeTrust)
        {
            var trust = new CheckBox { Content = new TextBlock { Text = Ui.T("Allow imported profiles to disable certificate or SSH host-key verification"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 16, 0, 8) };
            apply.IsEnabled = false;
            trust.Click += (_, _) => apply.IsEnabled = trust.IsChecked == true;
            panel.Children.Add(trust);
        }
    }
}
