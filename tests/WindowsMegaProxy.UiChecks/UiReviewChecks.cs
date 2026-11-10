using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsMegaProxy;

internal static partial class Program
{
    private static void CheckUiReview(MainWindow window, Action<bool> rejectSave)
    {
        var grid = (DataGrid)window.FindName("RulesList");
        Button Button(string name) => (Button)window.FindName(name);
        void Click(string name)
        {
            Button(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var pending = (Task)typeof(MainWindow).GetField("settingsUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            try { Wait(window, pending); } catch (System.IO.IOException) { }
            Layout(window);
        }
        grid.SelectedIndex = -1;
        Assert(!Button("EditRuleButton").IsEnabled && !Button("DeleteRuleButton").IsEnabled, "Selection commands are disabled without a selected rule.");
        grid.SelectedIndex = 0;
        Assert(!Button("MoveRuleUpButton").IsEnabled, "The first rule cannot move up.");
        grid.SelectedIndex = grid.Items.Count - 1;
        Assert(!Button("MoveRuleDownButton").IsEnabled, "The last rule cannot move down.");
        grid.SelectedIndex = 1;
        int before = grid.Items.Count;
        var original = ((RoutingRule)grid.SelectedItem).Copy();
        Click("DeleteRuleButton");
        Assert(grid.Items.Count == before - 1 && Button("UndoDeleteRuleButton").IsVisible, "A persisted deletion exposes undo.");
        rejectSave(true);
        Click("UndoDeleteRuleButton");
        Assert(grid.Items.Count == before - 1 && Button("UndoDeleteRuleButton").IsEnabled, "Failed undo keeps its recovery action and rolls back the view.");
        rejectSave(false);
        Click("UndoDeleteRuleButton");
        var restored = (RoutingRule)grid.Items[1];
        Assert(grid.Items.Count == before && restored.Name == original.Name && restored.Destination == original.Destination && restored.Enabled == original.Enabled,
            "Undo restores the rule and its priority without losing its parameters.");
        Assert(Button("UndoDeleteRuleButton").Visibility == Visibility.Collapsed, "Successful undo consumes the recovery action.");
        rejectSave(true);
        Click("DeleteRuleButton");
        Assert(grid.Items.Count == before && Button("UndoDeleteRuleButton").Visibility == Visibility.Collapsed, "Failed deletion does not offer phantom undo.");
        rejectSave(false);

        var rule = new RuleWindow(new RoutingRule { Destination = "podkop:youtube,discord" })
        { Height = 480, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        rule.Show(); Layout(rule);
        var pathBox = Descendants(rule).OfType<TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == Ui.T("Application path"));
        var pathRow = (Grid)pathBox.Parent;
        var browse = pathRow.Children.OfType<Button>().Single();
        Assert(Grid.GetColumn(browse) == 1 && pathRow.Margin.Top == 8, "Path browsing sits beside the input with a gap below source selection.");
        var cancel = Descendants(rule).OfType<Button>().Single(button => button.IsCancel);
        Assert(cancel.TransformToAncestor(rule).Transform(new Point()).Y + cancel.ActualHeight < rule.ActualHeight,
            "Podkop buttons remain in the viewport of a 480 DIP rule dialog.");
        Assert(Descendants(rule).OfType<ScrollViewer>().Any(viewer => viewer.ScrollableHeight > 0), "The long rule form scrolls instead of growing off-screen.");
        rule.Close();

        var profile = ProxyProfile.Create("UI validation fixture");
        foreach (string type in new[] { "SSH_JUMP", "HTTPS_JUMP", "MASQUE" })
        {
            var labelProfile = profile.Copy(); labelProfile.Proxy["type"] = type;
            Assert(!labelProfile.TypeLabel.Contains('_') && labelProfile.Description.Contains(labelProfile.TypeLabel),
                "Profile descriptions and pickers use readable protocol labels.");
        }
        profile.Proxy["host"] = "proxy.example";
        var editor = new ProfileWindow(profile) { Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        editor.Show(); Layout(editor);
        var port = (TextBox)editor.FindName("PortBox");
        port.Text = "invalid";
        typeof(ProfileWindow).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [editor, new RoutedEventArgs()]);
        Layout(editor);
        var error = (TextBlock)editor.FindName("ErrorText");
        Assert(error.Text.Length > 0 && error.TransformToAncestor(editor).Transform(new Point()).Y + error.ActualHeight < editor.ActualHeight,
            "Invalid port feedback is visible without scrolling to the bottom of the form.");
        Assert(port.IsKeyboardFocusWithin && editor.MinWidth >= 480, "Invalid input receives focus and the editor has a usable minimum width.");
        Assert(Descendants(editor).OfType<Expander>().Any(item => !item.IsExpanded && item.Header as string == Ui.T("Appearance")), "Appearance is secondary and collapsed initially.");
        Assert(editor.FindName("Http3HelpText") is TextBlock help && ReferenceEquals(help.Style, UiTheme.ExplanationStyle),
            "HTTP version help is an always-expanded explanation using the shared style.");
        editor.Close();

        string previousLanguage = Ui.Language;
        foreach (string language in new[] { "en", "ru" })
        {
            var export = new ExportWindow { Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            UiTheme.Apply(language, "light", export);
            export.Show(); Layout(export);
            var format = (ComboBox)export.FindName("FormatBox");
            var label = (Label)export.FindName("FormatLabel");
            string actual = UIElementAutomationPeer.CreatePeerForElement(format)!.GetName();
            Assert(actual == Ui.T("Format"), $"Export format UIA name ({language}): expected '{Ui.T("Format")}', got '{actual}'.");
            Assert(System.Windows.Automation.AutomationProperties.GetLabeledBy(format) == label && label.Target == format,
                "Export format keeps its programmatic label association.");
            export.Close();
        }
        UiTheme.Apply(previousLanguage, "dark", window);

        var text = new TextBlock { Height = 500, Width = 900 };
        var inner = new ScrollViewer { Content = text, Height = 100, Width = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel(); stack.Children.Add(inner); stack.Children.Add(new Border { Height = 600 });
        var outer = new ScrollViewer { Content = stack };
        var scrollWindow = new Window { Content = outer, Height = 300, Width = 400, Left = -10000, ShowInTaskbar = false };
        scrollWindow.Show(); Layout(scrollWindow);
        inner.ScrollToBottom(); Layout(scrollWindow);
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = text };
        WindowInput.MouseWheel(inner, wheel); Layout(scrollWindow);
        Assert(wheel.Handled && outer.VerticalOffset > 0, "A wheel event at the inner bottom scrolls the outer form.");
        inner.ScrollToTop(); Layout(scrollWindow);
        wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = text };
        WindowInput.MouseWheel(inner, wheel); Layout(scrollWindow);
        Assert(outer.VerticalOffset == 0, "A wheel event at the inner top also reaches the outer form.");
        scrollWindow.Close();

        window.Height = window.MinHeight; Layout(window);
        Assert(window.FindName("ConnectionLabel") is null, "Short windows automatically use a compact connection header.");
        window.Height = 740; Layout(window);
        Assert(window.FindName("ConnectionLabel") is null, "Growing the window keeps the redundant connection label removed.");

        var profiles = (ListBox)window.FindName("ProfilesList");
        var capture = typeof(MainWindow).GetMethod("CaptureSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var commit = typeof(MainWindow).GetMethod("CommitSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalSettings = (Settings)capture.Invoke(window, null)!;
        var reorderSettings = originalSettings.Copy();
        for (int i = 0; i < 2; i++)
        {
            var extra = ProxyProfile.Create("Order fixture " + i);
            extra.Proxy["host"] = "fixture.invalid";
            reorderSettings.Profiles.Add(extra);
        }
        Wait(window, (Task)commit.Invoke(window, [reorderSettings])!); Layout(window);
        var order = profiles.Items.Cast<ProxyProfile>().Select(item => item.Id).ToArray();
        Assert(order.Length > 1, "Reordering fixture needs multiple profiles.");
        string active = ((ProxyProfile)((ComboBox)window.FindName("ProfilePicker")).SelectedItem).Id;
        var move = typeof(MainWindow).GetMethod("MoveProfileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Move(string id, string? target, bool after)
        {
            try { Wait(window, (Task)move.Invoke(window, [id, target, after])!); } catch (System.IO.IOException) { }
            Layout(window);
        }
        Move(order[0], order[^1], true);
        Assert(((ProxyProfile)profiles.Items[^1]).Id == order[0] && ((ProxyProfile)((ComboBox)window.FindName("ProfilePicker")).SelectedItem).Id == active,
            "Reordering persists without changing the active profile.");
        rejectSave(true);
        Move(order[0], order[1], false);
        Assert(((ProxyProfile)profiles.Items[^1]).Id == order[0], "A failed reorder keeps the previously saved order.");
        rejectSave(false);
        Move(order[0], order[1], false);
        Assert(profiles.Items.Cast<ProxyProfile>().Select(item => item.Id).SequenceEqual(order), "Profiles can move up as well as down.");
        var access = typeof(MainWindow).GetField("canEdit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        access.SetValue(window, false);
        try
        {
            Move(order[0], order[^1], true);
            Assert(profiles.Items.Cast<ProxyProfile>().Select(item => item.Id).SequenceEqual(order), "Read-only clients cannot reorder profiles.");
        }
        finally { access.SetValue(window, true); }
        Wait(window, (Task)commit.Invoke(window, [originalSettings])!); Layout(window);

        var sections = (TabControl)window.FindName("Sections");
        int selected = sections.SelectedIndex;
        double width = window.Width;
        foreach (var entry in new[] { (Index: 0, Name: "ConnectionsGrid"), (Index: 2, Name: "RulesList") })
        {
            sections.SelectedIndex = entry.Index; window.Width = 1400; Layout(window);
            var table = (DataGrid)window.FindName(entry.Name);
            double total = table.Columns.Sum(column => column.ActualWidth);
            Assert(total >= table.ActualWidth - 40, $"{entry.Name} fills a wide viewport: columns={total}, viewport={table.ActualWidth}; widths={string.Join(",", table.Columns.Select(column => column.Width.ToString()))}; scroll={string.Join(",", Descendants(table).OfType<ScrollViewer>().Select(scroll => $"{scroll.ActualWidth}/{scroll.ViewportWidth}"))}.");
            window.Width = window.MinWidth; Layout(window);
            Assert(table.Columns.Sum(column => column.ActualWidth) < total, "Columns expand and shrink with the viewport.");
            if (entry.Index == 0)
            {
                var source = table.ItemsSource;
                table.ItemsSource = Array.Empty<ConnectionRow>();
                window.Width = 1300; Layout(window);
                Assert(table.Columns.Sum(column => column.ActualWidth) >= table.ActualWidth - 40, "Empty tables also fill the available width.");
                table.ItemsSource = source;
            }
        }
        window.Width = width; sections.SelectedIndex = selected; Layout(window);

        var minimizeToTray = (CheckBox)window.FindName("AlwaysMinimizeToTrayBox");
        void SetMinimizeToTray(bool value)
        {
            minimizeToTray.IsChecked = value;
            minimizeToTray.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            Layout(window);
        }
        Assert(minimizeToTray.IsChecked == false, "Minimizing to tray is opt-in.");
        rejectSave(true); SetMinimizeToTray(true);
        Assert(minimizeToTray.IsChecked == false, "A failed preference save restores the previous tray behavior.");
        rejectSave(false); SetMinimizeToTray(true);
        var traySettings = (Settings)capture.Invoke(window, null)!;
        Assert(traySettings.AlwaysMinimizeToTray, "Tray preference saves immediately.");
        var exportedTray = PortableConfiguration.Export(traySettings);
        Assert(PortableConfiguration.Import(exportedTray.ToJsonString(), new Settings()).AlwaysMinimizeToTray, "Tray preference survives configuration transfer.");
        exportedTray["windows"]!.AsObject().Remove("alwaysMinimizeToTray");
        Assert(PortableConfiguration.Import(exportedTray.ToJsonString(), traySettings).AlwaysMinimizeToTray, "An absent imported tray preference leaves the current setting intact.");
        window.WindowState = WindowState.Minimized; Layout(window);
        Assert(!window.IsVisible, "Enabled tray minimization hides the window.");
        typeof(MainWindow).GetMethod("ShowWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Layout(window);
        Assert(window.IsVisible && window.WindowState == WindowState.Normal, "The tray action restores the minimized window.");
        SetMinimizeToTray(false);
        window.WindowState = WindowState.Minimized; Layout(window);
        Assert(window.IsVisible, "Disabled tray minimization keeps the normal Windows minimized window.");
        window.WindowState = WindowState.Normal; Layout(window);

        var confirmQuit = typeof(MainWindow).GetMethod("ConfirmQuitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Wait(window, (Task)confirmQuit.Invoke(window, [new Func<bool>(() => false)])!);
        Assert(window.IsVisible && !(bool)typeof(MainWindow).GetField("quitting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
            "Declining the settings exit confirmation leaves the application running.");

        sections.SelectedIndex = 2;
        grid.SelectedIndex = -1;
        foreach (string theme in new[] { "light", "dark" })
        {
            UiTheme.Apply(Ui.Language, theme, window); Layout(window);
            var errorDialog = new MessageDialog(window, "Example error", "Error", confirmation: false);
            errorDialog.Loaded += (_, _) => errorDialog.Dispatcher.BeginInvoke(new Action(() =>
            {
                var ok = Descendants(errorDialog).OfType<Button>().Single();
                Assert(ok.IsDefault && ok.IsCancel && (string)ok.Content == "OK", "Errors expose one default, cancellable OK button.");
#pragma warning disable WPF0001
                Assert(errorDialog.ThemeMode == window.ThemeMode, "Errors follow the owner's theme.");
#pragma warning restore WPF0001
                ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }));
            errorDialog.ShowDialog();
            foreach (int choice in new[] { 0, 1, 2 })
            {
                var dialog = new MessageDialog(window, Ui.T("Exit MegaProxy?"), Ui.T("Exit"));
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Layout(dialog);
#pragma warning disable WPF0001
                    Assert(dialog.ThemeMode == window.ThemeMode, "Confirmations inherit the owner's explicit theme.");
#pragma warning restore WPF0001
                    var buttons = Descendants(dialog).OfType<Button>().ToArray();
                    var cancel = buttons.Single(button => button.IsCancel);
                    Assert(cancel.IsDefault && FocusManager.GetFocusedElement(dialog) == cancel, "Confirmation defaults and logical focus favor cancellation.");
                    if (choice == 2) dialog.Close();
                    else buttons.Single(button => button.IsCancel == (choice == 0)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }));
                Assert((dialog.ShowDialog() == true) == (choice == 1), "Only explicit confirmation accepts; No and window close cancel.");
            }
            var row = Descendants(grid).OfType<DataGridRow>().First();
            Assert(ReferenceEquals(grid.RowStyle, window.FindResource("TrafficRowStyle")), "Rules share the connection table's base row style.");
            Assert(row.Margin == new Thickness(0) && row.Padding == new Thickness(0), "Rule rows have no extra padding or gaps.");
            Assert(row.Background is System.Windows.Media.SolidColorBrush background && background.Color.A < 32,
                "Rule rows never inherit an opaque white background in the dark theme.");
            Assert(ReferenceEquals(row.Foreground, window.FindResource("TextFillColorPrimaryBrush")), "Rule text follows the active theme.");
        }
        sections.SelectedIndex = 1; Layout(window);
        Assert(Descendants(profiles).OfType<ContentControl>().Any(control => ReferenceEquals(control.ContentTemplate, window.FindResource("ProfileTypeBadgeTemplate"))),
            "Profile types appear in the shared outlined badge template.");
        var picker = (ComboBox)window.FindName("ProfilePicker");
        void CheckPickerAlignment(DependencyObject root)
        {
            var badge = Descendants(root).OfType<ContentControl>().First(control => ReferenceEquals(control.ContentTemplate, window.FindResource("ProfileTypeBadgeTemplate")));
            var parent = (Grid)System.Windows.Media.VisualTreeHelper.GetParent(badge);
            var label = parent.Children.OfType<TextBlock>().Single();
            var typeLabel = Descendants(badge).OfType<TextBlock>().Single();
            Assert(typeLabel.FontSize == 11 && label.FontSize == 14, "Picker protocol uses 11-point text without changing the profile name.");
            double nameBaseline = label.TranslatePoint(new Point(0, label.BaselineOffset), parent).Y;
            double typeBaseline = typeLabel.TranslatePoint(new Point(0, typeLabel.BaselineOffset), parent).Y;
            Assert(Math.Abs(nameBaseline - typeBaseline) < 0.1, "Profile name and protocol text share the same baseline.");
        }
        CheckPickerAlignment(picker);
        picker.IsDropDownOpen = true; Layout(window);
        CheckPickerAlignment((ComboBoxItem)picker.ItemContainerGenerator.ContainerFromIndex(0));
        picker.IsDropDownOpen = false;
        bool backgroundNoticeShown = false;
        var noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        noticeTimer.Tick += (_, _) =>
        {
            var notice = Application.Current.Windows.OfType<MessageDialog>().FirstOrDefault(dialog => dialog.Owner == window);
            if (notice is null) return;
            backgroundNoticeShown = true;
            noticeTimer.Stop();
            notice.Close();
        };
        noticeTimer.Start();
        window.Close();
        Assert(backgroundNoticeShown && !window.IsVisible && !(bool)typeof(MainWindow).GetField("quitting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
            "Closing the window explains background operation and hides it without exiting.");
        typeof(MainWindow).GetMethod("ShowWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    }
}
