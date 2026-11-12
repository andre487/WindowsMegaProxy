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
    private readonly Wpf.StackPanel[] pages = new Wpf.StackPanel[4];
    private readonly Dictionary<string, Wpf.RadioButton>[] choices = new Dictionary<string, Wpf.RadioButton>[4];
    private bool removing;
    private readonly Wpf.DockPanel licensePage = new();
    internal readonly WpfPrimitives.ToggleButton LicenseAcceptance = new();
    private readonly Label heading = new() { AutoSize = true, Margin = new Padding(0, 12, 0, 12) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(570, 0), Tag = "Explanation" };
    internal readonly WpfPrimitives.ToggleButton DesktopSwitch = new();
    internal readonly WpfPrimitives.ToggleButton LaunchSwitch = new();
    private readonly Button next = Button();
    private readonly Button back = Button();
    private readonly Button remove = Button();
    private readonly Button close = Button();
    internal readonly Wpf.ProgressBar InstallProgress = new() { Height = 6, Maximum = 100, Margin = new System.Windows.Thickness(0, 16, 0, 8) };
    internal readonly Wpf.TextBox InstallLog = new() { IsReadOnly = true, TextWrapping = System.Windows.TextWrapping.Wrap,
        VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto, FontSize = 12, Padding = new System.Windows.Thickness(12) };
    private readonly Wpf.DockPanel installation = new();
    private readonly Timer logTimer = new() { Interval = 500 };
    private readonly SetupLogReader logReader = new();
    private bool readingLog;
    private bool logDirectoryWarning;
    private string? burnLog;
    private DateTime logStart;
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
        AddChoice(0, "user", "PersonalScope", "PersonalScopeDescription", "\uE77B");
        AddChoice(0, "machine", "SystemScope", "SystemScopeDescription", "\uE716");
        AddChoice(1, "preserve", "PreserveMode", "PreserveModeDescription", "\uE713");
        AddChoice(1, "manual", "ManualMode", "ManualModeDescription", "\uE768");
        AddChoice(1, "login", "LoginMode", "LoginModeDescription", "\uE77B");
        AddChoice(1, "service", "ServiceMode", "ServiceModeDescription", "\uE770");
        AddChoice(2, "preserve", "PreserveUpdates", "PreserveUpdatesDescription", "\uE713");
        AddChoice(2, "true", "EnableUpdates", "EnableUpdatesDescription", "\uE895");
        AddChoice(2, "false", "DisableUpdates", "DisableUpdatesDescription", "\uE711");
        AddChoice(3, "0", "KeepData", "KeepDataDescription", "\uE74E");
        AddChoice(3, "1", "RemoveData", "RemoveDataDescription", "\uE74D");
        Select(3, "0");
        choices[0]["user"].Checked += (_, _) => UpdateServiceChoice();
        choices[0]["machine"].Checked += (_, _) => UpdateServiceChoice();
        void UpdateServiceChoice()
        {
            bool personal = Scope == BundleScope.PerUser;
            choices[1]["service"].Visibility = personal ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            if (personal && choices[1]["service"].IsChecked == true) Select(1, locked ? "preserve" : "manual");
        }
        void AddSwitch(WpfPrimitives.ToggleButton toggle, string label)
        {
            toggle.Content = new Wpf.TextBlock { Text = T(label), TextWrapping = System.Windows.TextWrapping.Wrap };
            System.Windows.Automation.AutomationProperties.SetName(toggle, T(label));
            pages[2].Children.Add(toggle);
        }
        var licenseDescription = new Wpf.TextBlock { Text = T("LicenseDescription"),
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 12) };
        licenseDescription.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "Secondary");
        licensePage.Resources = ChoicesView.Resources;
        licensePage.SetResourceReference(Wpf.Control.ForegroundProperty, "Foreground");
        Wpf.DockPanel.SetDock(licenseDescription, Wpf.Dock.Top);
        licensePage.Children.Add(licenseDescription);
        var licenseText = new Wpf.TextBox { IsReadOnly = true, FontSize = 12,
            TextWrapping = System.Windows.TextWrapping.Wrap, VerticalScrollBarVisibility = Wpf.ScrollBarVisibility.Auto,
            Padding = new System.Windows.Thickness(12) };
        foreach (string resource in new[] { "SetupLicense.txt", "ApplicationLicense.txt", "WixLicense.txt" })
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream!))
                licenseText.AppendText(reader.ReadToEnd() + "\r\n\r\n");
        licenseText.SetResourceReference(Wpf.Control.BackgroundProperty, "CardBackground");
        licenseText.SetResourceReference(Wpf.Control.ForegroundProperty, "Foreground");
        licenseText.SetResourceReference(Wpf.Control.BorderBrushProperty, "Border");
        UseSystemScrolling(licenseText);
        System.Windows.Automation.AutomationProperties.SetName(licenseText, T("LicenseTitle"));
        LicenseAcceptance.Content = new Wpf.TextBlock { Text = T("AcceptLicenses"), TextWrapping = System.Windows.TextWrapping.Wrap };
        LicenseAcceptance.SetResourceReference(Wpf.Control.ForegroundProperty, "Foreground");
        LicenseAcceptance.Margin = new System.Windows.Thickness(0, 16, 0, 8);
        System.Windows.Automation.AutomationProperties.SetName(LicenseAcceptance, T("AcceptLicenses"));
        LicenseAcceptance.Checked += (_, _) => { if (Step == -1) next.Enabled = true; };
        LicenseAcceptance.Unchecked += (_, _) => { if (Step == -1) next.Enabled = false; };
        Wpf.DockPanel.SetDock(LicenseAcceptance, Wpf.Dock.Bottom);
        licensePage.Children.Add(LicenseAcceptance);
        licensePage.Children.Add(licenseText);
        AddSwitch(DesktopSwitch, "DesktopShortcut");
        AddSwitch(LaunchSwitch, "LaunchAfterSetup");
        var footer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Bottom };
        footer.SizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(100, footer.ClientSize.Width), 0);
        footer.Controls.Add(status);
        installation.Resources = ChoicesView.Resources;
        Wpf.DockPanel.SetDock(InstallProgress, System.Windows.Controls.Dock.Bottom);
        installation.Children.Add(InstallProgress);
        installation.Children.Add(InstallLog);
        InstallLog.SetResourceReference(Wpf.Control.BackgroundProperty, "CardBackground");
        InstallLog.SetResourceReference(Wpf.Control.ForegroundProperty, "Foreground");
        InstallLog.SetResourceReference(Wpf.Control.BorderBrushProperty, "Border");
        UseSystemScrolling(InstallLog);
        System.Windows.Automation.AutomationProperties.SetName(InstallLog, T("SetupLog"));
        System.Windows.Automation.AutomationProperties.SetName(InstallProgress, T("ProgressHeader"));
        logTimer.Tick += async (_, _) =>
        {
            if (readingLog || burnLog is null) return;
            readingLog = true;
            try
            {
                string prefix = Scope == BundleScope.PerUser ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value : "machine";
                string batch = await System.Threading.Tasks.Task.Run(() => ReadLogs(prefix));
                if (!IsDisposed) AppendLog(batch);
            }
            finally { readingLog = false; if (IsDisposed) logReader.Dispose(); }
        };
        Disposed += (_, _) => { logTimer.Dispose(); if (!readingLog) logReader.Dispose(); };
        log.Text = T("SetupLog"); footer.Controls.Add(log);
        var license = new LinkLabel { Text = T("LicenseLink"), AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        license.LinkClicked += (_, _) => System.Diagnostics.Process.Start("https://github.com/andre487/WindowsMegaProxy/blob/main/LICENSING.md")?.Dispose();
        footer.Controls.Add(license);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        close.Text = T("InstallCancelButton"); back.Text = T("BackButton"); remove.Text = T("ModifyUninstallButton");
        next.Enabled = false; back.Visible = false; remove.Visible = false;
        next.Click += (_, _) => Advance(); back.Click += (_, _) => Previous();
        remove.Click += (_, _) => { if (removing) application?.Uninstall(); else Removal(); }; close.Click += (_, _) => Close();
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
    private static void UseSystemScrolling(Wpf.TextBox textBox)
    {
        textBox.PreviewMouseWheel += (_, args) =>
        {
            textBox.ScrollToVerticalOffset(textBox.VerticalOffset + WheelDistance(args.Delta,
                System.Windows.SystemParameters.WheelScrollLines,
                textBox.FontSize * textBox.FontFamily.LineSpacing, textBox.ViewportHeight));
            args.Handled = true;
        };
    }
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

    internal void Ready(bool installed, BundleScope selectedScope, Dictionary<string, string> arguments, bool scopeLocked = false, bool clientWasRunning = false)
    {
        this.installed = installed; locked = installed || scopeLocked;
        choices[1]["preserve"].Visibility = choices[2]["preserve"].Visibility = locked ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        Select(0, selectedScope == BundleScope.PerMachine ? "machine" : "user");
        pages[0].IsEnabled = !locked;
        Select(1, arguments.TryGetValue("Mode", out var mode) && (locked || mode != "preserve") ? mode : locked ? "preserve" : "manual");
        Select(2, arguments.TryGetValue("AutomaticUpdates", out var updates) && (locked || updates != "preserve") ? updates : locked ? "preserve" : "true");
        Select(3, arguments.TryGetValue("RemoveData", out var removal) ? removal : "0");
        DesktopSwitch.IsChecked = arguments.TryGetValue("DesktopShortcut", out var shortcut) && shortcut == "1";
        LaunchSwitch.IsChecked = arguments.TryGetValue("LaunchAfterSetup", out var launchValue) ? launchValue == "1" : locked && clientWasRunning;
        next.Enabled = true; remove.Visible = installed;
        ShowStep(-1);
    }

    private void ShowStep(int step)
    {
        Step = step;
        content.Child = step == -1 ? licensePage : ChoicesView;
        if (step >= 0) ChoicesView.Content = pages[step];
        ChoicesView.ScrollToTop();
        heading.Text = string.Format(CultureInfo.CurrentCulture, T("StepTitle"), step + 2,
            step == -1 ? T("LicenseTitle") : T(new[] { "SetupOptions", "OperatingMode", "AutomaticUpdates" }[step]));
        back.Visible = step >= 0;
        next.Enabled = LicenseAcceptance.IsChecked == true;
        next.Text = T(step < 2 ? "NextButton" : installed ? "ModifyRepairButton" : "InstallInstallButton");
        status.Text = step == 0 && locked ? T("ScopeLocked") : "";
        next.Focus();
    }

    internal void Advance() { if (LicenseAcceptance.IsChecked != true) return; if (Step < 2) ShowStep(Step + 1); else application?.Install(); }
    internal void Previous() { if (Step >= 0) ShowStep(Step - 1); }
    internal Dictionary<string, string> Options() => new()
    {
        ["Mode"] = Selected(1), ["AutomaticUpdates"] = Selected(2), ["RemoveData"] = Selected(3),
        ["DesktopShortcut"] = DesktopSwitch.IsChecked == true ? "1" : "0", ["LaunchAfterSetup"] = LaunchSwitch.IsChecked == true ? "1" : "0"
    };
    internal void Removal()
    {
        removing = true;
        content.Visible = true; content.Child = ChoicesView; ChoicesView.Content = pages[3]; ChoicesView.ScrollToTop();
        heading.Visible = true; heading.Text = T("ModifyUninstallButton").Replace("&", "");
        next.Visible = false; back.Visible = false; remove.Visible = true;
        AcceptButton = remove; status.Text = T("UninstallPrompt");
    }
    internal void Begin()
    {
        content.Child = installation; content.Visible = true; heading.Visible = true; heading.Text = T("ProgressHeader");
        next.Enabled = false; back.Enabled = false; remove.Enabled = false;
        status.Text = ""; InstallProgress.IsIndeterminate = true;
    }
    internal void FollowLog(string path) { burnLog = path; logStart = DateTime.UtcNow; logTimer.Start(); }
    private string ReadLogs(string prefix)
    {
        string result = logReader.Read(burnLog!);
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MegaProxy.Setup");
            if (Directory.Exists(directory))
                foreach (var file in new DirectoryInfo(directory).GetFiles(prefix + "-*.log").Where(file => file.LastWriteTimeUtc >= logStart).OrderBy(file => file.Name))
                    result += logReader.Read(file.FullName);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            if (!logDirectoryWarning) result += "Setup engine log is unavailable: " + error.Message + "\r\n";
            logDirectoryWarning = true;
        }
        return result;
    }
    internal void AppendLog(string text)
    {
        if (text.Length == 0) return;
        bool follow = InstallLog.VerticalOffset >= InstallLog.ExtentHeight - InstallLog.ViewportHeight - 1;
        if (text.Length > 131072) text = text.Substring(text.Length - 131072);
        int excess = InstallLog.Text.Length + text.Length - 131072;
        if (excess > 0) InstallLog.Text = InstallLog.Text.Substring(excess);
        InstallLog.AppendText(text);
        if (follow) InstallLog.ScrollToEnd();
    }
    internal void Progress(int value) { InstallProgress.IsIndeterminate = false; InstallProgress.Value = Math.Max(0, Math.Min(100, value)); }
    internal void CancelPending() { close.Enabled = false; status.Text = T("ConfirmCancelMessage"); }
    internal void LegacyInstallation() { status.Text = T("InnoInstallationDetected"); }
    internal void Help() { status.Text = T("HelpText"); }
    internal void Complete(int code, string path)
    {
        InstallProgress.IsIndeterminate = false;
        if (code == 0 || code == 3010) InstallProgress.Value = 100;
        status.Text = code == 0 ? T("SuccessHeader") : code == 3010 ? T("SuccessRestartText") : T("FailureHeader") + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
        close.Enabled = true; close.Text = T("SuccessCloseButton"); next.Enabled = false; back.Enabled = false; remove.Enabled = false;
        log.Visible = true;
        log.LinkClicked += (_, _) => System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\"")?.Dispose();
    }
}
