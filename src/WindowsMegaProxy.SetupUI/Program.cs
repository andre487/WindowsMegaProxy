using System;
using System.Collections.Generic;
using System.ComponentModel;
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

internal static class Program
{
    // Burn initializes COM as MTA; BootstrapperApplication creates its own STA UI thread.
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--ui-checks")
        {
            int result = 1;
            var uiThread = new System.Threading.Thread(() => result = CheckUi(args[1]));
            uiThread.SetApartmentState(System.Threading.ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
            return result;
        }
        ManagedBootstrapperApplication.Run(new SetupApplication());
        return 0;
    }

    private static int CheckUi(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            Application.EnableVisualStyles();
            IntPtr native = LoadLibrary(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mbanative.dll"));
            if (native == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            FreeLibrary(native);
            IntPtr callback = Marshal.GetComInterfaceForObject(new SetupApplication(), typeof(IBootstrapperApplication));
            Marshal.Release(callback);
            foreach (string language in new[] { "en", "ru" })
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo(language);
                using var window = new SetupWindow(null, "0.0.0");
                window.CheckUi(directory, language);
            }
            File.Delete(Path.Combine(directory, "failure.txt"));
            File.WriteAllText(Path.Combine(directory, "result.txt"), "Native setup UI language, scope and maintenance checks passed.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "failure.txt"), error.ToString()); return 1; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
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
    private readonly Dictionary<string, string> arguments = new(StringComparer.Ordinal);
    internal string Version => engine.GetVariableString("WixBundleVersion");

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        command = args.Command;
        engine.SetVariableString("OwnerSid", WindowsIdentity.GetCurrent().User!.Value, false);
        try
        {
            foreach (var item in command.ParseCommandLine().Variables)
            {
                if (item.Key == "Scope")
                {
                    requestedScope = item.Value switch { "PerUser" => BundleScope.PerUser, "PerMachine" => BundleScope.PerMachine, _ => throw new ArgumentException("Invalid installation scope.") };
                    continue;
                }
                if (item.Key is not ("Mode" or "AutomaticUpdates" or "InstallSSH" or "DesktopShortcut" or "LaunchAfterSetup"))
                    throw new ArgumentException("Unknown setup variable: " + item.Key);
                Validate(item.Key, item.Value);
                arguments[item.Key] = item.Value;
            }
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
        if (command.Display != Display.None) window.Show();
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
        installed = args.RegistrationType == RegistrationType.Full;
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
            if (installed && requestedScope != BundleScope.Default && requestedScope != detected)
            {
                engine.Log(LogLevel.Error, "Uninstall the existing scope before installing another scope. Configuration is retained.");
                Complete(unchecked((int)0x80070666));
                return;
            }
            var scope = detected != BundleScope.Default ? detected : requestedScope != BundleScope.Default ? requestedScope
                : relatedScope != BundleScope.Default ? relatedScope : BundleScope.PerMachine;
            if (scope == BundleScope.PerUser && arguments.TryGetValue("Mode", out var mode) && mode == "service")
                throw new ArgumentException("Windows service mode is unavailable for a personal installation.");
            window.Ready(installed, scope, arguments);
            if (command.Display != Display.Full) Start(command.Action == LaunchAction.Install && installed ? LaunchAction.Repair : command.Action);
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
                Validate(item.Key, item.Value);
                engine.SetVariableString(item.Key, item.Value, false);
            }
            engine.SetVariableNumeric("IsUninstall", action == LaunchAction.Uninstall ? 1 : 0);
            busy = true;
            cancel = false;
            window.Begin();
            engine.Plan(action, window.Scope);
        }
        catch (Exception error) { engine.Log(LogLevel.Error, error.Message); Complete(unchecked((int)0x80070057)); }
    }

    private static void Validate(string name, string value)
    {
        bool valid = name switch
        {
            "Mode" => value is "preserve" or "manual" or "login" or "service",
            "AutomaticUpdates" => value is "preserve" or "true" or "false",
            _ => value is "0" or "1"
        };
        if (!valid) throw new ArgumentException("Invalid setup option: " + name);
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
        window.Complete(code, engine.GetVariableString("WixBundleLog"));
        if (command.Display != Display.Full) { window.Close(); Application.ExitThread(); }
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
    internal void Install() => Start(installed ? LaunchAction.Repair : LaunchAction.Install);
    internal void Uninstall() => Start(LaunchAction.Uninstall);
}

internal sealed class SetupWindow : Form
{
    private readonly Dictionary<string, string> text;
    private readonly ComboBox scope = Choice();
    private readonly ComboBox mode = Choice();
    private readonly ComboBox updates = Choice();
    private readonly CheckBox ssh = new() { AutoSize = true, Checked = true };
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
        Font = new Font("Segoe UI", 9F);
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
        ssh.Text = T("InstallSSH"); desktop.Text = T("DesktopShortcut"); launch.Text = T("LaunchAfterSetup");
        foreach (var check in new[] { ssh, desktop, launch }) { check.Margin = new Padding(0, 7, 0, 0); options.Controls.Add(check); }
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
    }

    private string T(string key) => text[key];
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 570, Margin = new Padding(0, 3, 0, 10) };
    private void AddOption(string label, Control control)
    {
        options.Controls.Add(new Label { Text = label, AutoSize = true });
        options.Controls.Add(control);
    }

    internal void Ready(bool installed, BundleScope selectedScope, Dictionary<string, string> arguments)
    {
        scope.SelectedIndex = selectedScope == BundleScope.PerUser ? 1 : 0;
        scope.Enabled = !installed;
        mode.SelectedIndex = 0;
        updates.SelectedIndex = 0;
        foreach (var item in arguments)
        {
            switch (item.Key)
            {
                case "Mode": mode.SelectedIndex = Array.IndexOf(Modes, item.Value); break;
                case "AutomaticUpdates": updates.SelectedIndex = Array.IndexOf(Updates, item.Value); break;
                case "InstallSSH": ssh.Checked = item.Value == "1"; break;
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
        ["InstallSSH"] = ssh.Checked ? "1" : "0", ["DesktopShortcut"] = desktop.Checked ? "1" : "0", ["LaunchAfterSetup"] = launch.Checked ? "1" : "0"
    };

    internal void Begin() { options.Enabled = false; install.Enabled = false; remove.Enabled = false; status.Text = T("ProgressHeader"); progress.Visible = true; progress.Style = ProgressBarStyle.Marquee; }
    internal void Progress(int value) { progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, value)); }
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

    internal void CheckUi(string directory, string language)
    {
        Show();
        Ready(false, BundleScope.PerMachine, new Dictionary<string, string>());
        Application.DoEvents();
        if (Scope != BundleScope.PerMachine || mode.Items.Count != 4 || !ssh.Checked) throw new Exception("System setup defaults changed.");
        Save("system");
        scope.SelectedIndex = 1;
        Application.DoEvents();
        if (Scope != BundleScope.PerUser || mode.Items.Count != 3) throw new Exception("Personal setup must exclude service mode.");
        Save("personal");
        Ready(true, BundleScope.PerUser, new Dictionary<string, string> { ["AutomaticUpdates"] = "false" });
        if (scope.Enabled || Options()["AutomaticUpdates"] != "false" || !remove.Visible) throw new Exception("Maintenance must preserve scope and allow opt-out.");
        Close();

        void Save(string scopeName)
        {
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(Path.Combine(directory, language + "-" + scopeName + ".png"));
        }
    }
}
