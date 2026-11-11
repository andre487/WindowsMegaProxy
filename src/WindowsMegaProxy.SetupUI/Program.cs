using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
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
    private bool Silent => command.Display != Display.Full;
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
            if (action != LaunchAction.Uninstall && action != LaunchAction.Layout && InnoInstallation.Exists())
            {
                engine.Log(LogLevel.Error, InnoInstallation.Message);
                Complete(unchecked((int)0x80070666));
                window.LegacyInstallation();
                return;
            }
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
            if (parsed.Boot)
            {
                string hash = engine.GetVariableString("SetupEngineSha256");
                // OriginalSource is persisted by Burn and can point at the first installer download.
                string bundle = engine.GetVariableString("WixBundleSourceProcessPath");
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
