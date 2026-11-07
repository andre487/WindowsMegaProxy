using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private bool settingsNavigationBusy;
    private string? settingsPage;

    private async void SettingsCategoryClick(object sender, RoutedEventArgs e) =>
        await NavigateSettingsAsync((string)((Button)sender).Tag);

    private async void SettingsBackClick(object sender, RoutedEventArgs e) => await NavigateSettingsAsync(null);

    private async void SettingsNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.SystemKey != Key.Left || Keyboard.Modifiers != ModifierKeys.Alt || settingsPage is null) return;
        e.Handled = true;
        await NavigateSettingsAsync(null);
    }

    private Task FlushSettingsFormAsync() => SettingsDetailHost.Content switch
    {
        SubscriptionForm subscription => subscription.FlushAsync(),
        FailoverForm failover => failover.FlushAsync(),
        _ => Task.CompletedTask
    };

    private async Task NavigateSettingsAsync(string? page)
    {
        if (settingsNavigationBusy || quitting || page == settingsPage) return;
        settingsNavigationBusy = true;
        string? previous = settingsPage;
        SettingsCategories.IsEnabled = SettingsBackButton.IsEnabled = SettingsRootButton.IsEnabled = false;
        SettingsDetailHost.IsEnabled = false;
        try
        {
            await FlushSettingsFormAsync();
            if (quitting) return;
            settingsPage = page;
            SettingsDetailHost.Content = null;
            foreach (var form in new FrameworkElement[] { AppearanceSettings, StartupSettings, FingerprintSettings, LogSettings, DynamicSettingsCard })
                form.Visibility = Visibility.Collapsed;
            string? title = null;
            FrameworkElement? selected = null;
            switch (page)
            {
                case "appearance": title = "Appearance"; selected = AppearanceSettings; break;
                case "startup": title = "Application startup"; selected = StartupSettings; break;
                case "fingerprints": title = "TLS fingerprints"; selected = FingerprintSettings; break;
                case "log": title = "Log"; selected = LogSettings; break;
                case "subscription":
                    title = "Configuration subscription"; selected = DynamicSettingsCard;
                    var subscription = CreateSubscriptionForm();
                    subscription.Removed += async () => await NavigateSettingsAsync(null);
                    SettingsDetailHost.Content = subscription;
                    break;
                case "failover":
                    title = "Failover"; selected = DynamicSettingsCard;
                    SettingsDetailHost.Content = CreateFailoverForm();
                    break;
                case null: break;
                default: throw new ArgumentException("Unknown settings category.");
            }
            bool overview = page is null;
            SettingsCategories.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
            SettingsForms.Visibility = SettingsBreadcrumbSeparator.Visibility = SettingsPageTitle.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
            if (selected is not null) selected.Visibility = Visibility.Visible;
            if (title is not null) SettingsPageTitle.SetResourceReference(TextBlock.TextProperty, title);
            SettingsRootButton.SetResourceReference(Control.ForegroundProperty, overview ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            SettingsScroll.ScrollToTop();
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            settingsNavigationBusy = false;
            SettingsCategories.IsEnabled = SettingsRootButton.IsEnabled = true;
            SettingsBackButton.IsEnabled = settingsPage is not null;
            SetBusy(busy);
        }
        if (settingsPage is null && previous is not null)
            ((Button?)FindName(char.ToUpperInvariant(previous[0]) + previous[1..] + "CategoryButton"))?.Focus();
        else SettingsBackButton.Focus();
    }
}
