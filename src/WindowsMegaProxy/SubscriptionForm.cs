using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowsMegaProxy;

public sealed class SubscriptionForm : UserControl
{
    private readonly TextBox url = new(), backups = new() { AcceptsReturn = true, Height = 84, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox username = new(), interval = new() { Text = "60" };
    private readonly PasswordBox password = new();
    private readonly CheckBox auth = new() { Content = Ui.T("Use Basic authentication") }, enabled = new() { Content = Ui.T("Automatic updates"), IsChecked = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly Func<SubscriptionDefinition?, Task> save;
    private bool saving, removed;
    private int revision, savedRevision;
    public event Action? Removed;

    public SubscriptionForm(Settings settings, Func<SubscriptionDefinition?, Task> save, Func<Task<Settings>> refresh, Func<bool>? confirmRemoval = null)
    {
        this.save = save;
        var panel = new StackPanel();
        Content = panel;
        Add(panel, "HTTPS URL", url); Add(panel, "Backup HTTPS URLs (one per line, up to 7)", backups);
        var explanation = new TextBlock { Text = Ui.T("Trust every source. All sources receive the same credentials and may update profiles, settings and source URLs. Redirects are rejected."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(explanation);
        panel.Children.Add(auth); Add(panel, "Username", username); Add(panel, "Password", password);
        Add(panel, "Update interval (minutes, 1–10080)", interval); panel.Children.Add(enabled); panel.Children.Add(status);
        var update = new Button { Content = Ui.T("Update now"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        update.Margin = new Thickness(0, 8, 8, 8);
        actions.Children.Add(update);
        var remove = new Button { Content = Ui.T("Remove subscription (keep profiles)"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(remove);
        var definition = ConfigurationSubscription.Get(settings);
        if (definition is not null)
        {
            url.Text = definition.Url; backups.Text = string.Join(Environment.NewLine, definition.FallbackUrls);
            username.Text = definition.Username ?? ""; password.Password = definition.Password ?? "";
            auth.IsChecked = definition.Username is not null || definition.Password is not null;
            interval.Text = definition.IntervalMinutes.ToString(); enabled.IsChecked = definition.Enabled;
        }
        ShowStatus(settings);
        username.IsEnabled = password.IsEnabled = auth.IsChecked == true;
        foreach (var box in new[] { url, backups, username, interval }) box.TextChanged += (_, _) => Changed();
        password.PasswordChanged += (_, _) => Changed();
        enabled.Click += (_, _) => Changed();
        auth.Click += (_, _) => { username.IsEnabled = password.IsEnabled = auth.IsChecked == true; Changed(); };
        debounce.Tick += async (_, _) => { debounce.Stop(); await SaveAsync(); };
        update.Click += async (_, _) =>
        {
            if (!await SaveAsync()) return;
            update.IsEnabled = false;
            try { ShowStatus(await refresh()); }
            catch (Exception) { status.Text = Ui.T("Update failed"); }
            finally { update.IsEnabled = true; }
        };
        remove.Click += async (_, _) =>
        {
            bool confirmed = confirmRemoval?.Invoke() ?? MessageDialog.Confirm(Window.GetWindow(this), Ui.T("Remove the subscription? Existing profiles will be kept."),
                Ui.T("Configuration subscription"));
            if (!confirmed) return;
            debounce.Stop();
            while (saving) await Task.Delay(20);
            saving = true;
            try { await save(null); removed = true; }
            catch (Exception) { status.Text = Ui.T("Could not save subscription settings."); }
            finally { saving = false; debounce.Stop(); }
            if (removed) Removed?.Invoke();
        };
    }

    public async Task FlushAsync()
    {
        debounce.Stop();
        while (saving) await Task.Delay(20);
        if (!removed && revision != savedRevision && url.Text.Length > 0) await SaveAsync();
        debounce.Stop();
    }

    private void Changed() { revision++; debounce.Stop(); debounce.Start(); }
    private async Task<bool> SaveAsync()
    {
        if (removed) return true;
        if (saving) { debounce.Start(); return false; }
        saving = true;
        int expected = revision;
        try
        {
            if (!int.TryParse(interval.Text, out int minutes)) throw new ArgumentException();
            var definition = new SubscriptionDefinition(url.Text.Trim(), backups.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToArray(),
                auth.IsChecked == true ? username.Text : null, auth.IsChecked == true ? password.Password : null, minutes, enabled.IsChecked == true);
            _ = SubscriptionDefinition.Parse(definition.ToJson());
            await save(definition);
            savedRevision = expected;
            status.Text = Ui.T("Subscription settings saved.");
            return expected == revision;
        }
        catch (Exception) { status.Text = Ui.T("Enter valid HTTPS sources and an interval from 1 to 10080 minutes."); return false; }
        finally { saving = false; if (expected != revision) debounce.Start(); }
    }

    private void ShowStatus(Settings settings)
    {
        long success = ConfigurationSubscription.LastSuccess(settings);
        int source = ConfigurationSubscription.SourceIndex(settings);
        status.Text = Ui.T(ConfigurationSubscription.Status(settings)) + (source < 0 ? "" : " · " + Ui.F("Source {0}", source + 1))
            + (success == 0 ? "" : " · " + DateTimeOffset.FromUnixTimeSeconds(success).ToLocalTime().ToString("g"));
    }

    private static void Add(StackPanel panel, string label, Control control)
    {
        panel.Children.Add(new Label { Content = Ui.T(label), Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(control);
    }
}
