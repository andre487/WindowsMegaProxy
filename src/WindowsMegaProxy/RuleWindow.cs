using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public sealed class RuleWindow : Window
{
    private readonly ComboBox source = new() { ItemsSource = new[] { Ui.T("All applications"), Ui.T("EXE file"), Ui.T("Folder (including subfolders)") }, SelectedIndex = 0 };
    private readonly TextBox path = new() { IsReadOnly = true };
    private readonly TextBox destination = new();
    private readonly ComboBox action = new() { ItemsSource = new[] { Ui.T("Active profile"), Ui.T("System mode"), Ui.T("Block") }, SelectedIndex = 0 };
    public RoutingRule? Rule { get; private set; }

    public RuleWindow(RoutingRule? existingRule = null)
    {
        Title = existingRule is null ? Ui.T("New rule") : Ui.T("Edit rule");
        Width = 550;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
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
        source.SelectionChanged += (_, _) => { path.Clear(); browse.IsEnabled = source.SelectedIndex != 0; };
        browse.IsEnabled = false;
        AutomationProperties.SetName(path, Ui.T("Application path"));
        panel.Children.Add(path);
        panel.Children.Add(browse);
        AddField(panel, Ui.T("Destination: domain, *.domain, IP or CIDR; empty means any"), destination);
        AddField(panel, Ui.T("Action"), action);
        if (existingRule is not null)
        {
            source.SelectedIndex = existingRule.Source switch { "exe" => 1, "folder" => 2, _ => 0 };
            path.Text = existingRule.Path;
            destination.Text = existingRule.Destination;
            action.SelectedIndex = existingRule.Action switch { "system" or "direct" => 1, "block" => 2, _ => 0 };
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var save = new Button { Content = existingRule is null ? Ui.T("Add") : Ui.T("Save"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var rule = new RoutingRule { Source = new[] { "all", "exe", "folder" }[source.SelectedIndex], Path = path.Text,
                Destination = destination.Text.Trim(), Action = new[] { "proxy", "system", "block" }[action.SelectedIndex] };
            try { Configuration.BuildRule(rule); Rule = rule; DialogResult = true; }
            catch (ArgumentException error) { MessageBox.Show(this, error.Message, Ui.T("Rule")); }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = Ui.T("Cancel"), IsCancel = true });
        panel.Children.Add(buttons);
    }
    private static void AddField(StackPanel panel, string label, Control control)
    {
        panel.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(control);
    }
}
