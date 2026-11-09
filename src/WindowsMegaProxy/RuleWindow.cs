using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public sealed class RuleWindow : Window
{
    private readonly ComboBox source = new() { ItemsSource = new[] { Ui.T("All applications"), Ui.T("EXE file"), Ui.T("Folder (including subfolders)") }, SelectedIndex = 0 };
    private readonly TextBox path = new() { IsEnabled = false };
    private readonly TextBox destination = new();
    private readonly ComboBox destinationType = new() { ItemsSource = new[] { Ui.T("Domain, IP or subnet"), "Podkop" }, SelectedIndex = 0 };
    private readonly Dictionary<string, CheckBox> podkop = [];
    private readonly CheckBox podkopThroughProxy = new() { Content = Ui.T("Update Podkop lists through proxy"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox name = new() { MaxLength = 80 };
    private readonly CheckBox enabled = new() { Content = Ui.T("Enabled"), IsChecked = true, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox action = new() { ItemsSource = new[] { Ui.T("Active profile"), Ui.T("System mode"), Ui.T("Block") }, SelectedIndex = 0 };
    public RoutingRule? Rule { get; private set; }

    public RuleWindow(RoutingRule? existingRule = null)
    {
        Title = existingRule is null ? Ui.T("New rule") : Ui.T("Edit rule");
        Width = 550;
        MinWidth = 500;
        Height = Math.Min(680, SystemParameters.WorkArea.Height);
        MinHeight = Math.Min(400, Height);
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        AddField(panel, Ui.T("Rule name (optional)"), name);
        panel.Children.Add(enabled);
        AddField(panel, Ui.T("Application"), source);
        var browse = new Button { Content = Ui.T("Browse…"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
        browse.Click += (_, _) =>
        {
            if (source.SelectedIndex == 1)
            {
                var dialog = new OpenFileDialog { Filter = Ui.T("Windows applications|*.exe"), CheckFileExists = true };
                if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName;
            }
            else if (source.SelectedIndex == 2)
            {
                var dialog = new OpenFolderDialog();
                if (dialog.ShowDialog(this) == true) path.Text = dialog.FolderName;
            }
        };
        source.SelectionChanged += (_, _) => { path.Clear(); path.IsEnabled = browse.IsEnabled = source.SelectedIndex != 0; };
        browse.IsEnabled = false;
        AutomationProperties.SetName(path, Ui.T("Application path"));
        panel.Children.Add(path);
        panel.Children.Add(browse);
        AddField(panel, Ui.T("Destination"), destinationType);
        AddField(panel, Ui.T("Destination: domain, *.domain, IP or CIDR; empty means any"), destination);
        var lists = new StackPanel();
        foreach (var list in PodkopCatalog.Lists)
        {
            var box = new CheckBox { Content = list.Name, Margin = new Thickness(0, 3, 0, 3) };
            podkop.Add(list.Id, box); lists.Children.Add(box);
        }
        var listScroll = new ScrollViewer { Content = lists, Height = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        panel.Children.Add(listScroll);
        panel.Children.Add(podkopThroughProxy);
        destinationType.SelectionChanged += (_, _) =>
        {
            destination.IsEnabled = destinationType.SelectedIndex == 0;
            listScroll.Visibility = destinationType.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            podkopThroughProxy.Visibility = listScroll.Visibility;
        };
        AddField(panel, Ui.T("Action"), action);
        if (existingRule is not null)
        {
            name.Text = existingRule.Name;
            podkopThroughProxy.IsChecked = existingRule.PodkopThroughProxy;
            enabled.IsChecked = existingRule.Enabled;
            source.SelectedIndex = existingRule.Source switch { "exe" => 1, "folder" => 2, _ => 0 };
            path.Text = existingRule.Path;
            destination.Text = existingRule.Destination;
            if (existingRule.Destination.StartsWith("podkop:", StringComparison.Ordinal))
            {
                destinationType.SelectedIndex = 1; destination.Clear();
                foreach (string id in PodkopCatalog.Parse(existingRule.Destination)) podkop[id].IsChecked = true;
            }
            action.SelectedIndex = existingRule.Action switch { "system" or "direct" => 1, "block" => 2, _ => 0 };
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var save = new Button { Content = existingRule is null ? Ui.T("Add") : Ui.T("Save"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var rule = new RoutingRule { Name = name.Text.Trim(), Enabled = enabled.IsChecked == true, Source = new[] { "all", "exe", "folder" }[source.SelectedIndex], Path = path.Text.Trim().Trim('"'),
                Destination = destinationType.SelectedIndex == 1 ? "podkop:" + string.Join(',', podkop.Where(item => item.Value.IsChecked == true).Select(item => item.Key)) : destination.Text.Trim(), Action = new[] { "proxy", "system", "block" }[action.SelectedIndex] };
            rule.PodkopThroughProxy = destinationType.SelectedIndex == 1 && podkopThroughProxy.IsChecked == true;
            try { Configuration.BuildRule(rule); Rule = rule; DialogResult = true; }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, Ui.T("Rule")); }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = Ui.T("Cancel"), IsCancel = true });
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
    }
    private static void AddField(StackPanel panel, string label, Control control)
    {
        panel.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(control);
    }
}
