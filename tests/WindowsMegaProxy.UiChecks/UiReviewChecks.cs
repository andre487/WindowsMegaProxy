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
        var cancel = Descendants(rule).OfType<Button>().Single(button => button.IsCancel);
        Assert(cancel.TransformToAncestor(rule).Transform(new Point()).Y + cancel.ActualHeight < rule.ActualHeight,
            "Podkop buttons remain in the viewport of a 480 DIP rule dialog.");
        Assert(Descendants(rule).OfType<ScrollViewer>().Any(viewer => viewer.ScrollableHeight > 0), "The long rule form scrolls instead of growing off-screen.");
        rule.Close();

        var profile = ProxyProfile.Create("UI validation fixture");
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
        Assert(((TextBlock)window.FindName("ConnectionLabel")).Visibility == Visibility.Collapsed, "Short windows automatically use a compact connection header.");
        window.Height = 740; Layout(window);
        Assert(((TextBlock)window.FindName("ConnectionLabel")).Visibility == Visibility.Visible, "Growing the window restores the full connection header.");
    }
}
