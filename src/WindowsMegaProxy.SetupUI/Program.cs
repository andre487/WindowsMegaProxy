using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using System.Xml.Linq;
using WixToolset.BootstrapperApplicationApi;

internal static class BootstrapEntry
{
    // Burn initializes COM as MTA; BootstrapperApplication creates its own STA UI thread.
    [MTAThread]
    private static int Main()
    {
        ManagedBootstrapperApplication.Run(new SetupApplication());
        return 0;
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SetupApplication : BootstrapperApplication
{
    private IBootstrapperCommand command = null!;
    private SetupWindow window = null!;
    private bool installed, busy, cancel, downgrade;
    private BundleScope relatedScope;
    private BundleScope requestedScope;
    private int exitCode;
    private SetupArguments parsed = null!;
    private Dictionary<string, string> arguments => parsed.Values;
    private bool Silent => parsed.Silent || command.Display != Display.Full;
    internal string Version => engine.GetVariableString("WixBundleVersion");

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        command = args.Command;
        engine.SetVariableString("OwnerSid", WindowsIdentity.GetCurrent().User!.Value, false);
        try
        {
            parsed = new SetupArguments(SetupArguments.Split(command.CommandLine));
            requestedScope = parsed.Scope;
            if (requestedScope == BundleScope.Default) requestedScope = command.Scope;
        }
        catch (Exception error) { engine.Log(LogLevel.Error, error.ToString()); args.HResult = unchecked((int)0x80070057); }
    }

    protected override void Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        window = new SetupWindow(this, Version);
        _ = window.Handle;
        window.FormClosing += (_, args) =>
        {
            if (busy) { args.Cancel = true; cancel = true; window.CancelPending(); }
        };
        window.FormClosed += (_, _) => Application.ExitThread();
        if (!Silent) window.Show();
        if (command.Action == LaunchAction.Help)
        {
            window.Help();
            if (command.Display == Display.None) { engine.Quit(0); return; }
        }
        else engine.Detect();
        Application.Run();
        engine.Quit(exitCode);
    }

    protected override void OnDetectBegin(DetectBeginEventArgs args)
    {
        installed = args.RegistrationType != RegistrationType.None;
        base.OnDetectBegin(args);
    }

    protected override void OnDetectRelatedBundle(DetectRelatedBundleEventArgs args)
    {
        if (args.RelationType == RelationType.Upgrade)
        {
            relatedScope = args.PerMachine ? BundleScope.PerMachine : BundleScope.PerUser;
            if (System.Version.TryParse(args.Version, out var previous) && System.Version.TryParse(Version, out var current)) downgrade |= previous > current;
        }
        base.OnDetectRelatedBundle(args);
    }

    protected override void OnDetectComplete(DetectCompleteEventArgs args)
    {
        base.OnDetectComplete(args);
        Post(() =>
        {
            if (args.Status < 0) { Complete(args.Status); return; }
            BundleScope detected = installed ? (BundleScope)engine.GetVariableNumeric("WixBundleDetectedScope") : BundleScope.Default;
            BundleScope existingScope = detected != BundleScope.Default ? detected : relatedScope;
            if (existingScope != BundleScope.Default && requestedScope != BundleScope.Default && requestedScope != existingScope)
            {
                engine.Log(LogLevel.Error, "Uninstall the existing scope before installing another scope. Configuration is retained.");
                Complete(unchecked((int)0x80070666));
                return;
            }
            var scope = detected != BundleScope.Default ? detected : requestedScope != BundleScope.Default ? requestedScope
                : relatedScope != BundleScope.Default ? relatedScope : BundleScope.PerMachine;
            if (scope == BundleScope.PerUser && arguments.TryGetValue("Mode", out var mode) && mode == "service")
                throw new ArgumentException("Windows service mode is unavailable for a personal installation.");
            window.Ready(installed, scope, arguments, relatedScope != BundleScope.Default);
            if (command.Action == LaunchAction.Uninstall) window.Removal();
            if (Silent) Start(command.Action == LaunchAction.Install && installed ? LaunchAction.Repair : command.Action);
        });
    }

    internal void Start(LaunchAction action)
    {
        try
        {
            if (downgrade && action != LaunchAction.Uninstall) throw new InvalidOperationException("A newer MegaProxy version is installed.");
            if (engine.GetVariableNumeric("WindowsBuildNumber") < 17763) throw new InvalidOperationException("Windows 10 version 1809 or later is required.");
            foreach (var item in window.Options())
            {
                SetupArguments.Validate(item.Key, item.Value);
                engine.SetVariableString(item.Key, item.Value, false);
            }
            engine.SetVariableNumeric("IsUninstall", action == LaunchAction.Uninstall ? 1 : 0);
            busy = true;
            cancel = false;
            window.Begin();
            engine.SetVariableString("SetupAction", arguments.TryGetValue("RegistrationOnly", out string registration) && registration == "1" ? "register" : "install", false);
            engine.SetVariableString("SetupPayloadPath", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload.zip"), false);
            engine.SetVariableString("StagedUpdate", arguments.TryGetValue("StagedUpdate", out string staged) ? staged : "0", false);
            if (parsed.Directory is not null)
            {
                string expected = window.Scope == BundleScope.PerUser
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsMegaProxy")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsMegaProxy");
                if (!string.Equals(parsed.Directory.TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("MegaProxy requires its fixed installation directory.");
            }
            if (parsed.Boot)
            {
                string hash = engine.GetVariableString("SetupEngineSha256");
                string bundle = engine.GetVariableString("WixBundleOriginalSource");
                System.Threading.Tasks.Task.Run(() =>
                {
                    int result;
                    try { result = BootLayout.Stage(AppDomain.CurrentDomain.BaseDirectory, hash, bundle); }
                    catch (Exception error) { engine.Log(LogLevel.Error, error.ToString()); result = 1; }
                    Post(() => Complete(result));
                });
                return;
            }
            engine.Plan(action, window.Scope);
        }
        catch (Exception error) { engine.Log(LogLevel.Error, error.Message); Complete(unchecked((int)0x80070057)); }
    }

    protected override void OnPlanComplete(PlanCompleteEventArgs args)
    {
        base.OnPlanComplete(args);
        if (args.Status < 0) Post(() => Complete(args.Status));
        else Post(() => engine.Apply(window.Handle));
    }

    protected override void OnProgress(ProgressEventArgs args)
    {
        args.Cancel |= cancel;
        Post(() => window.Progress(args.OverallPercentage));
        base.OnProgress(args);
    }

    protected override void OnCacheAcquireProgress(CacheAcquireProgressEventArgs args)
    {
        args.Cancel |= cancel;
        base.OnCacheAcquireProgress(args);
    }

    protected override void OnExecuteProgress(ExecuteProgressEventArgs args)
    {
        args.Cancel |= cancel;
        base.OnExecuteProgress(args);
    }

    protected override void OnApplyComplete(ApplyCompleteEventArgs args)
    {
        base.OnApplyComplete(args);
        Post(() => Complete(args.Status < 0 ? args.Status : args.Restart != ApplyRestart.None ? 3010 : 0));
    }

    private void Complete(int code)
    {
        busy = false;
        exitCode = code;
        string logPath = engine.GetVariableString("WixBundleLog");
        if (parsed.LogPath is not null)
        {
            try { using var input = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var output = File.Create(parsed.LogPath); input.CopyTo(output); }
            catch (Exception error) { engine.Log(LogLevel.Error, "Could not copy setup log: " + error.Message); }
        }
        window.Complete(code, logPath);
        if (Silent) { window.Close(); Application.ExitThread(); }
    }

    private void Post(Action action)
    {
        if (window.IsDisposed) return;
        window.BeginInvoke(new Action(() =>
        {
            try { action(); }
            catch (Exception error) { engine.Log(LogLevel.Error, error.Message); Complete(unchecked((int)0x80070057)); }
        }));
    }
    internal void Install() => Start(command.Action == LaunchAction.Layout ? LaunchAction.Layout : installed ? LaunchAction.Repair : LaunchAction.Install);
    internal void Uninstall() => Start(LaunchAction.Uninstall);
}

internal sealed partial class SetupWindow : Form
{
    private readonly Dictionary<string, string> text;
    private readonly ComboBox scope = Choice();
    private readonly ComboBox mode = Choice();
    private readonly ComboBox updates = Choice();
    private readonly CheckBox desktop = new() { AutoSize = true };
    private readonly CheckBox launch = new() { AutoSize = true };
    private readonly Button install = new() { AutoSize = true, Enabled = false, Padding = new Padding(12, 4, 12, 4) };
    private readonly Button remove = new() { AutoSize = true, Visible = false, Padding = new Padding(12, 4, 12, 4) };
    private readonly Button close = new() { AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(570, 0), Padding = new Padding(0, 8, 0, 8) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 18, Visible = false };
    private readonly TableLayoutPanel options = new() { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 8) };
    private LinkLabel log = null!;
    private static readonly string[] Modes = { "preserve", "manual", "login", "service" };
    private static readonly string[] Updates = { "preserve", "true", "false" };
    internal BundleScope Scope => scope.SelectedIndex == 1 ? BundleScope.PerUser : BundleScope.PerMachine;

    internal SetupWindow(SetupApplication? application, string version)
    {
        bool russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(russian ? "ru.wxl" : "en.wxl"))
            text = XDocument.Load(stream!).Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Value")!);
        Text = "MegaProxy";
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(24);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Width = 590 };
        layout.Controls.Add(new Label { Text = "MegaProxy", Font = new Font(Font.FontFamily, 18F), AutoSize = true });
        layout.Controls.Add(new Label { Text = version, AutoSize = true, ForeColor = SystemColors.GrayText });
        scope.Items.AddRange(new object[] { T("SystemScope"), T("PersonalScope") });
        mode.Items.AddRange(new object[] { T("PreserveMode"), T("ManualMode"), T("LoginMode"), T("ServiceMode") });
        updates.Items.AddRange(new object[] { T("PreserveUpdates"), T("EnableUpdates"), T("DisableUpdates") });
        AddOption(T("SetupOptions"), scope);
        AddOption(T("OperatingMode"), mode);
        AddOption(T("AutomaticUpdates"), updates);
        desktop.Text = T("DesktopShortcut"); launch.Text = T("LaunchAfterSetup");
        foreach (var check in new[] { desktop, launch }) { check.Margin = new Padding(0, 7, 0, 0); options.Controls.Add(check); }
        scope.SelectedIndexChanged += (_, _) =>
        {
            if (Scope == BundleScope.PerUser && mode.Items.Count == 4)
            {
                if (mode.SelectedIndex == 3) mode.SelectedIndex = 0;
                mode.Items.RemoveAt(3);
            }
            else if (Scope == BundleScope.PerMachine && mode.Items.Count == 3) mode.Items.Add(T("ServiceMode"));
        };
        layout.Controls.Add(options);
        var license = new LinkLabel { Text = T("LicenseLink"), AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        license.LinkClicked += (_, _) => System.Diagnostics.Process.Start("https://github.com/andre487/WindowsMegaProxy/blob/main/LICENSE")?.Dispose();
        layout.Controls.Add(license);
        layout.Controls.Add(status);
        layout.Controls.Add(progress);
        log = new LinkLabel { Text = T("SetupLog"), AutoSize = true, Visible = false };
        layout.Controls.Add(log);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 16, 0, 0) };
        install.Text = T("InstallInstallButton"); remove.Text = T("ModifyUninstallButton"); close.Text = T("InstallCancelButton");
        install.Click += (_, _) => application?.Install(); remove.Click += (_, _) => application?.Uninstall(); close.Click += (_, _) => Close();
        buttons.Controls.AddRange(new Control[] { close, install, remove });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = close;
        status.Text = T("OverallProgressPackageText");
        SetupTheme.Apply(this);
    }

    private string T(string key) => text[key];
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 570, Margin = new Padding(0, 3, 0, 10) };
    private void AddOption(string label, Control control)
    {
        options.Controls.Add(new Label { Text = label, AutoSize = true });
        options.Controls.Add(control);
    }

    internal void Ready(bool installed, BundleScope selectedScope, Dictionary<string, string> arguments, bool scopeLocked = false)
    {
        scope.SelectedIndex = selectedScope == BundleScope.PerUser ? 1 : 0;
        scope.Enabled = !installed && !scopeLocked;
        mode.SelectedIndex = 0;
        updates.SelectedIndex = 0;
        foreach (var item in arguments)
        {
            switch (item.Key)
            {
                case "Mode": mode.SelectedIndex = Array.IndexOf(Modes, item.Value); break;
                case "AutomaticUpdates": updates.SelectedIndex = Array.IndexOf(Updates, item.Value); break;
                case "DesktopShortcut": desktop.Checked = item.Value == "1"; break;
                case "LaunchAfterSetup": launch.Checked = item.Value == "1"; break;
            }
        }
        install.Enabled = true;
        install.Text = T(installed ? "ModifyRepairButton" : "InstallInstallButton");
        remove.Visible = installed;
        status.Text = T(installed ? "ScopeLocked" : "InstallHeader");
    }

    internal Dictionary<string, string> Options() => new()
    {
        ["Mode"] = mode.SelectedIndex < 0 ? "invalid" : Modes[mode.SelectedIndex],
        ["AutomaticUpdates"] = updates.SelectedIndex < 0 ? "invalid" : Updates[updates.SelectedIndex],
        ["DesktopShortcut"] = desktop.Checked ? "1" : "0", ["LaunchAfterSetup"] = launch.Checked ? "1" : "0"
    };

    internal void Removal()
    {
        options.Visible = false; install.Visible = false; remove.Visible = true;
        AcceptButton = remove; status.Text = T("UninstallPrompt");
    }

    internal void Begin() { options.Enabled = false; install.Enabled = false; remove.Enabled = false; status.Text = T("ProgressHeader"); progress.Visible = true; progress.Style = ProgressBarStyle.Marquee; }
    internal void Progress(int value) { progress.Style = value == 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, value)); }
    internal void CancelPending() { close.Enabled = false; status.Text = T("ConfirmCancelMessage"); }
    internal void Help() { status.Text = T("HelpText"); }
    internal void Complete(int code, string path)
    {
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = code == 0 ? 100 : 0;
        status.Text = code == 0 ? T("SuccessHeader") : code == 3010 ? T("SuccessRestartText") : T("FailureHeader") + " (0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
        close.Enabled = true; close.Text = T("SuccessCloseButton"); install.Enabled = false; remove.Enabled = false;
        log.Visible = true;
        log.LinkClicked += (_, _) => System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\"")?.Dispose();
    }

}
