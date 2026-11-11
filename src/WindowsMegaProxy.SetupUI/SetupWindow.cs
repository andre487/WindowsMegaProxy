using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Microsoft.Win32;
using Wpf = System.Windows.Controls;
using WpfPrimitives = System.Windows.Controls.Primitives;
using System.Xml.Linq;
using WixToolset.BootstrapperApplicationApi;

internal sealed class SetupWindow : Form
{
    private readonly Dictionary<string, string> text;
    private readonly SetupApplication? application;
    private readonly ElementHost content = new() { Dock = DockStyle.Fill };
    internal readonly Wpf.ScrollViewer ChoicesView = new()
    {
        VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Wpf.ScrollBarVisibility.Disabled,
        PanningMode = Wpf.PanningMode.VerticalOnly,
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14
    };
    private readonly Wpf.StackPanel[] pages = new Wpf.StackPanel[3];
    private readonly Dictionary<string, Wpf.RadioButton>[] choices = new Dictionary<string, Wpf.RadioButton>[3];
    private readonly Label heading = new() { AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(570, 0), Tag = "Explanation" };
    internal readonly WpfPrimitives.ToggleButton DesktopSwitch = new();
    internal readonly WpfPrimitives.ToggleButton LaunchSwitch = new();
    private readonly Button next = Button();
    private readonly Button back = Button();
    private readonly Button remove = Button();
    private readonly Button close = Button();
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 18, Visible = false };
    private readonly LinkLabel log = new() { AutoSize = true, Visible = false };
    private bool installed, locked;
    internal int Step { get; private set; }
    internal BundleScope Scope => Selected(0) == "user" ? BundleScope.PerUser : BundleScope.PerMachine;
    private static Button Button() => new() { AutoSize = true, Padding = new Padding(12, 4, 12, 4) };

    internal SetupWindow(SetupApplication? application, string version)
    {
        this.application = application;
        bool russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(russian ? "ru.wxl" : "en.wxl"))
            text = XDocument.Load(stream!).Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Value")!);
        Text = "MegaProxy";
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        Font = new Font("Segoe UI", 10F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 730);
        MinimumSize = new Size(560, 460);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(24);
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        header.Controls.Add(new Label { Text = "MegaProxy", Font = new Font(Font.FontFamily, 18F), AutoSize = true });
        header.Controls.Add(new Label { Text = version, AutoSize = true, Tag = "Explanation" });
        heading.Font = new Font("Segoe UI Semibold", 11F);
        header.Controls.Add(heading);
        header.SizeChanged += (_, _) => heading.MaximumSize = new Size(Math.Max(100, header.ClientSize.Width), 0);
        using (var styles = Assembly.GetExecutingAssembly().GetManifestResourceStream("WizardStyles.xaml"))
            ChoicesView.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(styles!);
        ChoicesView.SetResourceReference(Wpf.Control.ForegroundProperty, "Foreground");
        ChoicesView.SetResourceReference(Wpf.Control.BackgroundProperty, "Background");
        content.Child = ChoicesView;
        ChoicesView.PreviewMouseWheel += (_, args) =>
        {
            ChoicesView.ScrollToVerticalOffset(ChoicesView.VerticalOffset + WheelDistance(args.Delta,
                System.Windows.SystemParameters.WheelScrollLines,
                ChoicesView.FontSize * ChoicesView.FontFamily.LineSpacing, ChoicesView.ViewportHeight));
            args.Handled = true;
        };
        for (int i = 0; i < pages.Length; i++)
        {
            choices[i] = new Dictionary<string, Wpf.RadioButton>();
            pages[i] = new Wpf.StackPanel { Margin = new System.Windows.Thickness(0, 0, 8, 8) };
        }
        AddChoice(0, "machine", "SystemScope", "SystemScopeDescription", "\uE716");
        AddChoice(0, "user", "PersonalScope", "PersonalScopeDescription", "\uE77B");
        AddChoice(1, "preserve", "PreserveMode", "PreserveModeDescription", "\uE713");
        AddChoice(1, "manual", "ManualMode", "ManualModeDescription", "\uE768");
        AddChoice(1, "login", "LoginMode", "LoginModeDescription", "\uE77B");
        AddChoice(1, "service", "ServiceMode", "ServiceModeDescription", "\uE770");
        AddChoice(2, "preserve", "PreserveUpdates", "PreserveUpdatesDescription", "\uE713");
        AddChoice(2, "true", "EnableUpdates", "EnableUpdatesDescription", "\uE895");
        AddChoice(2, "false", "DisableUpdates", "DisableUpdatesDescription", "\uE711");
        choices[0]["user"].Checked += (_, _) => UpdateServiceChoice();
        choices[0]["machine"].Checked += (_, _) => UpdateServiceChoice();
        void UpdateServiceChoice()
        {
            bool personal = Scope == BundleScope.PerUser;
            choices[1]["service"].Visibility = personal ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            if (personal && choices[1]["service"].IsChecked == true) Select(1, "preserve");
        }
        void AddSwitch(WpfPrimitives.ToggleButton toggle, string label)
        {
            toggle.Content = new Wpf.TextBlock { Text = T(label), TextWrapping = System.Windows.TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetName(toggle, T(label));
            pages[2].Children.Add(toggle);
        }
        AddSwitch(DesktopSwitch, "DesktopShortcut");
        AddSwitch(LaunchSwitch, "LaunchAfterSetup");
        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Bottom };
        footer.SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(100, footer.ClientSize.Width), 0);
        footer.Controls.Add(status);
        footer.Controls.Add(progress);
        log.Text = T("SetupLog"); footer.Controls.Add(log);
        var license = new LinkLabel { Text = T("LicenseLink"), AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        license.LinkClicked += (_, _) => System.Diagnostics.Process.Start("https://github.com/andre487/WindowsMegaProxy/blob/main/LICENSE")?.Dispose();
        footer.Controls.Add(license);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        close.Text = T("InstallCancelButton"); back.Text = T("BackButton"); remove.Text = T("ModifyUninstallButton");
        next.Enabled = false; back.Visible = false; remove.Visible = false;
        next.Click += (_, _) => Advance(); back.Click += (_, _) => Previous();
        remove.Click += (_, _) => application?.Uninstall(); close.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { close, next, back, remove }); footer.Controls.Add(buttons);
        Controls.Add(content); Controls.Add(header); Controls.Add(footer);
        AcceptButton = next; CancelButton = close;
        status.Text = T("OverallProgressPackageText");
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        Disposed += (_, _) => SystemEvents.UserPreferenceChanged -= ThemeChanged;
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height - 24));
        };
    }

    private string T(string key) => text[key];
    // WPF pixel scrolling uses oversized lines; match text height and the Windows wheel preference.
    internal static double WheelDistance(int delta, int lines, double lineHeight, double viewport) =>
        -delta / 120.0 * (lines < 0 ? viewport : lines * lineHeight);
    private string Selected(int page) => choices[page].FirstOrDefault(pair => pair.Value.IsChecked == true).Key ?? "invalid";
    internal void Select(int page, string value)
    {
        if (!choices[page].TryGetValue(value, out var radio)) throw new ArgumentException("Invalid setup choice.");
        if (page == 1 && value == "service" && Scope == BundleScope.PerUser) throw new ArgumentException("Personal installations cannot use service mode.");
        radio.IsChecked = true;
    }

    private void ApplyTheme()
    {
        SetupTheme.Apply(this);
        SetupTheme.ApplyChoices(ChoicesView.Resources);
    }

    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) ApplyTheme(); }));
    }

    private void AddChoice(int page, string value, string title, string description, string glyph)
    {
        var details = new Wpf.StackPanel();
        details.Children.Add(new Wpf.TextBlock { Text = T(title), FontSize = 15, TextWrapping = System.Windows.TextWrapping.Wrap });
        var explanation = new Wpf.TextBlock { Text = T(description), Margin = new System.Windows.Thickness(0, 6, 0, 0),
            TextWrapping = System.Windows.TextWrapping.Wrap, FontSize = 13 };
        explanation.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "Secondary");
        details.Children.Add(explanation);
        var radio = new Wpf.RadioButton { Content = details, Tag = glyph, GroupName = "Step" + page };
        System.Windows.Automation.AutomationProperties.SetName(radio, T(title));
        System.Windows.Automation.AutomationProperties.SetHelpText(radio, T(description));
        radio.Checked += (_, _) =>
        {
            foreach (var other in choices[page].Values) if (other != radio) other.IsChecked = false;
        };
        radio.KeyDown += (_, args) =>
        {
            int offset = args.Key is System.Windows.Input.Key.Down or System.Windows.Input.Key.Right ? 1
                : args.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Left ? -1 : 0;
            if (offset == 0) return;
            var available = choices[page].Values.Where(item => item.Visibility == System.Windows.Visibility.Visible && item.IsEnabled).ToArray();
            if (available.Length == 0) return;
            var target = available[(Array.IndexOf(available, radio) + offset + available.Length) % available.Length];
            target.IsChecked = true; target.Focus(); args.Handled = true;
        };
        choices[page].Add(value, radio);
        pages[page].Children.Add(radio);
    }

    internal void Ready(bool installed, BundleScope selectedScope, Dictionary<string, string> arguments, bool scopeLocked = false)
    {
        this.installed = installed; locked = installed || scopeLocked;
        Select(0, selectedScope == BundleScope.PerUser ? "user" : "machine");
        pages[0].IsEnabled = !locked;
        Select(1, arguments.TryGetValue("Mode", out var mode) ? mode : "preserve");
        Select(2, arguments.TryGetValue("AutomaticUpdates", out var updates) ? updates : "preserve");
        DesktopSwitch.IsChecked = arguments.TryGetValue("DesktopShortcut", out var shortcut) && shortcut == "1";
        LaunchSwitch.IsChecked = arguments.TryGetValue("LaunchAfterSetup", out var launchValue) && launchValue == "1";
        next.Enabled = true; remove.Visible = installed;
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        Step = step;
        ChoicesView.Content = pages[step];
        ChoicesView.ScrollToTop();
        heading.Text = string.Format(CultureInfo.CurrentCulture, T("StepTitle"), step + 1,
            T(new[] { "SetupOptions", "OperatingMode", "AutomaticUpdates" }[step]));
        back.Visible = step > 0;
        next.Text = T(step < 2 ? "NextButton" : installed ? "ModifyRepairButton" : "InstallInstallButton");
        status.Text = step == 0 && locked ? T("ScopeLocked") : "";
        next.Focus();
    }

    internal void Advance() { if (Step < 2) ShowStep(Step + 1); else application?.Install(); }
    internal void Previous() { if (Step > 0) ShowStep(Step - 1); }
    internal Dictionary<string, string> Options() => new()
    {
        ["Mode"] = Selected(1), ["AutomaticUpdates"] = Selected(2),
        ["DesktopShortcut"] = DesktopSwitch.IsChecked == true ? "1" : "0", ["LaunchAfterSetup"] = LaunchSwitch.IsChecked == true ? "1" : "0"
    };
    internal void Removal()
    {
        content.Visible = false; heading.Visible = false; next.Visible = false; back.Visible = false; remove.Visible = true;
        AcceptButton = remove; status.Text = T("UninstallPrompt");
    }
    internal void Begin()
    {
        content.Visible = false; heading.Visible = false; next.Enabled = false; back.Enabled = false; remove.Enabled = false;
        status.Text = T("ProgressHeader"); progress.Visible = true; progress.Style = ProgressBarStyle.Marquee;
    }
    internal void Progress(int value) { progress.Style = value == 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, value)); }
    internal void CancelPending() { close.Enabled = false; status.Text = T("ConfirmCancelMessage"); }
    internal void Help() { status.Text = T("HelpText"); }
    internal void Complete(int code, string path)
    {
        progress.Style = ProgressBarStyle.Continuous; progress.Value = code == 0 ? 100 : 0;
        status.Text = code == 0 ? T("SuccessHeader") : code == 3010 ? T("SuccessRestartText") : T("FailureHeader") + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
        close.Enabled = true; close.Text = T("SuccessCloseButton"); next.Enabled = false; back.Enabled = false; remove.Enabled = false;
        log.Visible = true;
        log.LinkClicked += (_, _) => System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\"")?.Dispose();
    }
}
