using System;
using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private (RoutingRule Rule, int Index)? deletedRule;
    private bool changingRules;

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ConnectionCard is null) return;
        bool compact = ActualHeight < 720;
        RootLayout.Margin = new Thickness(compact ? 16 : 24);
        TitleLayout.Margin = new Thickness(0, 0, 0, compact ? 4 : 16);
        AppTitle.SetResourceReference(StyleProperty, compact ? "SubtitleTextBlockStyle" : "TitleTextBlockStyle");
        ConnectionCard.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(20, 16, 20, 16);
        ConnectionCard.Margin = new Thickness(0, 0, 0, compact ? 8 : 16);
        ConnectionLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
