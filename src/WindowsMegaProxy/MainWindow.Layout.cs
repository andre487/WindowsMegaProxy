using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private (RoutingRule Rule, int Index)? deletedRule;
    private bool changingRules;
    private void AlignProfilePickerBaseline(object sender, RoutedEventArgs e)
    {
        var row = (Grid)sender;
        var name = row.Children.OfType<TextBlock>().Single();
        var badge = row.Children.OfType<ContentControl>().Single();
        var type = Descendants<TextBlock>(badge).FirstOrDefault();
        if (type is null || !row.IsLoaded) return;
        double difference = name.TranslatePoint(new Point(0, name.BaselineOffset), row).Y
            - type.TranslatePoint(new Point(0, type.BaselineOffset), row).Y;
        if (Math.Abs(difference) < 0.01) return;
        double offset = (badge.RenderTransform as System.Windows.Media.TranslateTransform)?.Y ?? 0;
        badge.RenderTransform = new System.Windows.Media.TranslateTransform(0, offset + difference);
    }

    private readonly System.Collections.Generic.HashSet<DataGrid> pendingTableWidths = [];
    private readonly System.Collections.Generic.Dictionary<DataGridColumn, (double Weight, double Applied)> flexibleColumns = [];

    private void TableSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) RefreshTableWidths((DataGrid)sender);
    }

    private void TableLoaded(object sender, RoutedEventArgs e) => RefreshTableWidths((DataGrid)sender);

    private void RefreshTableWidths(DataGrid table)
    {
        if (!pendingTableWidths.Add(table)) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            pendingTableWidths.Remove(table);
            if (table.ActualWidth <= 0) return;
            foreach (var column in table.Columns)
            {
                if (column.Width.IsStar) flexibleColumns[column] = (column.Width.Value, double.NaN);
                else if (flexibleColumns.TryGetValue(column, out var previous) &&
                    (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - previous.Applied) > 0.5))
                    flexibleColumns.Remove(column); // Keep widths the user explicitly resized.
            }
            var columns = table.Columns.Where(column => column.Visibility == Visibility.Visible && flexibleColumns.ContainsKey(column)).ToArray();
            double weights = columns.Sum(column => flexibleColumns[column].Weight);
            double minimum = columns.Sum(column => Math.Max(column.MinWidth, table.MinColumnWidth));
            double fixedWidth = table.Columns.Where(column => column.Visibility == Visibility.Visible && !flexibleColumns.ContainsKey(column)).Sum(column => column.ActualWidth);
            double extra = Math.Max(0, table.ActualWidth - table.BorderThickness.Left - table.BorderThickness.Right -
                table.RowHeaderActualWidth - SystemParameters.VerticalScrollBarWidth - fixedWidth - minimum);
            foreach (var column in columns)
            {
                double weight = flexibleColumns[column].Weight;
                double width = Math.Min(column.MaxWidth, Math.Max(column.MinWidth, table.MinColumnWidth) + extra * weight / weights);
                // Explicit pixels also fill empty Fluent grids, which have no cells panel to size star columns.
                flexibleColumns[column] = (weight, width);
                column.Width = new DataGridLength(width);
            }
        }));
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ConnectionCard is null) return;
        bool compact = ActualHeight < 720;
        RootLayout.Margin = new Thickness(compact ? 16 : 24);
        ConnectionCard.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(20, 16, 20, 16);
        ConnectionCard.Margin = new Thickness(0, 0, 0, compact ? 8 : 16);
    }

    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateListCommands();

    private void UpdateListCommands()
    {
        if (UndoDeleteRuleButton is null) return;
        bool editable = canEdit && !quitting && !settingsLoading && !installingAppUpdate;
        bool profileSelected = editable && ProfilesList.SelectedItem is ProxyProfile;
        EditProfileButton.IsEnabled = CloneProfileButton.IsEnabled = DeleteProfileButton.IsEnabled = profileSelected;
        bool ruleSelected = editable && !changingRules && RulesList.SelectedItem is RoutingRule;
        EditRuleButton.IsEnabled = DeleteRuleButton.IsEnabled = ruleSelected;
        MoveRuleUpButton.IsEnabled = ruleSelected && RulesList.SelectedIndex > 0;
        MoveRuleDownButton.IsEnabled = ruleSelected && RulesList.SelectedIndex < rules.Count - 1;
        UndoDeleteRuleButton.Visibility = deletedRule is null ? Visibility.Collapsed : Visibility.Visible;
        UndoDeleteRuleButton.IsEnabled = editable && !changingRules && deletedRule is not null;
        if (deletedRule is { } removed) UndoDeleteRuleButton.ToolTip = Ui.F("Restore rule: {0}", removed.Rule.NameLabel);
    }

    private async void DeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit || changingRules || RulesList.SelectedItem is not RoutingRule rule) return;
        int index = RulesList.SelectedIndex;
        changingRules = true;
        SetBusy(busy);
        try
        {
            var updated = CaptureSettings();
            updated.Rules.RemoveAt(index);
            await CommitSettingsAsync(updated);
            deletedRule = (rule.Copy(), index);
        }
        catch (Exception error) { ShowError(error); }
        finally { changingRules = false; SetBusy(busy); }
    }

    private async void UndoDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (!canEdit || changingRules || deletedRule is not { } removed) return;
        changingRules = true;
        SetBusy(busy);
        try
        {
            var updated = CaptureSettings();
            int index = Math.Min(removed.Index, updated.Rules.Count);
            updated.Rules.Insert(index, removed.Rule.Copy());
            await CommitSettingsAsync(updated);
            deletedRule = null;
            RulesList.SelectedIndex = index;
        }
        catch (Exception error) { ShowError(error); }
        finally { changingRules = false; SetBusy(busy); }
    }
}
