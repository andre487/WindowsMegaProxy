using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using WixToolset.BootstrapperApplicationApi;

internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string root = Path.Combine(Path.GetTempPath(), "MegaProxy.Setup.Check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Console.WriteLine("Checking setup commands, permissions, archives and runtime caching.");
            Arguments();
            LaunchDefaults();
            string oldInventory = "[\"launcher/WindowsMegaProxy.Launcher.exe\",\"launcher/coreclr.dll\",\"WindowsMegaProxy.dll\"]";
            string[] newFiles = { "launcher/windowsmegaproxy.launcher.exe" };
            Require(Program.ObsoleteLauncherFiles(oldInventory, newFiles, true).SequenceEqual(new[] { "launcher/coreclr.dll" }),
                "Launcher upgrades must remove only obsolete inventoried launcher files, ignoring filename case.");
            Require(Program.ObsoleteLauncherFiles(null, newFiles, true).Length == 0 &&
                Program.ObsoleteLauncherFiles(oldInventory, newFiles, false).Length == 0,
                "Fresh and system installs must not remove personal launcher files.");
            Permissions();
            UserFiles(root);
            Archives(root);
            string logFile = Path.Combine(root, "live.log");
            using (var reader = new SetupLogReader())
            {
                Require(reader.Read(logFile) == "", "A not-yet-created log must be allowed.");
                File.WriteAllText(logFile, "First error\r\n", new System.Text.UTF8Encoding(false));
                Require(reader.Read(logFile).Contains("First error"), "Live log must read existing output.");
                Require(reader.Read(logFile) == "", "Live log must not repeat old output.");
                File.AppendAllText(logFile, "Rollback error\r\n");
                Require(reader.Read(logFile).Contains("Rollback error"), "Live log must follow appended output.");
            }
            Program.DeleteInstalledFile(Path.Combine(root, "missing-launcher", "clretwrc.dll"));
            string rollbackFile = Path.Combine(root, "rollback-file.dll");
            File.WriteAllText(rollbackFile, "partial installation");
            Program.DeleteInstalledFile(rollbackFile);
            Require(!File.Exists(rollbackFile), "Rollback must delete a partially installed file.");
            Program.DeleteInstalledFile(rollbackFile);
            Reject<UnauthorizedAccessException>(() => Program.DeleteInstalledFile(root));
            string data = Path.Combine(root, "personal-data");
            Directory.CreateDirectory(Path.Combine(data, "cache"));
            File.WriteAllText(Path.Combine(data, "settings.json"), "fixture");
            File.WriteAllText(Path.Combine(data, "cache", "list.txt"), "fixture");
            Program.DeleteDataTree(data);
            Program.DeleteDataTree(data);
            Require(!Directory.Exists(data) && Directory.Exists(root), "Full removal must delete only the selected data directory.");
            Runtime(root).GetAwaiter().GetResult();
            if (args.Length == 1) { Console.WriteLine("Checking setup wizard."); Windows(args[0]); }
            Console.WriteLine("Setup command compatibility, archive boundaries, runtime caching and UI checks passed; no installation performed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void UserFiles(string root)
    {
        using var original = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(original).IsInRole(WindowsBuiltInRole.Administrator);
        string folder = Path.Combine(root, "launcher");
        Program.Unelevated(() =>
        {
            using var current = WindowsIdentity.GetCurrent();
            Require(current.User == original.User, "User file operations must retain the installing user's identity.");
            Require(!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator), "User file operations must not retain administrator rights, including on CI.");
            Require(current.ImpersonationLevel >= TokenImpersonationLevel.Impersonation, "The token must support filesystem impersonation.");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "fixture.txt"), "launcher");
            Require(File.ReadAllText(Path.Combine(folder, "fixture.txt")) == "launcher", "Restricted user file writes and reads must work.");
        });
        Reject<IOException>(() => Program.Unelevated(() => throw new IOException("Synthetic user operation failure")));
        using var restored = WindowsIdentity.GetCurrent();
        Require(new WindowsPrincipal(restored).IsInRole(WindowsBuiltInRole.Administrator) == administrator && restored.ImpersonationLevel == original.ImpersonationLevel,
            "Both successful and failing operations must restore the caller's token.");
        Program.Unelevated(() => Directory.Delete(folder, true));
    }

    private static void LaunchDefaults()
    {
        string name = @"Local\MegaProxy.Setup.Check-" + Guid.NewGuid().ToString("N");
        Require(!SetupApplication.IsClientRunning(name), "A missing client instance must not enable launch.");
        bool wasRunning;
        using (var instance = new System.Threading.Mutex(false, name))
            wasRunning = SetupApplication.IsClientRunning(name);
        Require(wasRunning && !SetupApplication.IsClientRunning(name), "Observe the client without retaining its mutex after it exits.");
        foreach (var scope in new[] { BundleScope.PerUser, BundleScope.PerMachine })
        {
            using var window = new SetupWindow(null, "0.0.0");
            var defaults = new Dictionary<string, string>();
            window.Ready(false, scope, defaults, clientWasRunning: wasRunning);
            Require(window.Options()["LaunchAfterSetup"] == "0", "Fresh install defaults must not depend on a running client.");
            window.Ready(true, scope, defaults);
            Require(window.Options()["LaunchAfterSetup"] == "0", "Do not launch a client that was stopped before maintenance.");
            window.Ready(true, scope, defaults, clientWasRunning: wasRunning);
            Require(window.Options()["LaunchAfterSetup"] == "1", "Repair must restore a previously running client even after it exits.");
            window.Ready(false, scope, defaults, scopeLocked: true, clientWasRunning: wasRunning);
            Require(window.Options()["LaunchAfterSetup"] == "1", "Upgrade from a related bundle must restore a previously running client.");
            window.LaunchSwitch.IsChecked = false;
            Require(window.Options()["LaunchAfterSetup"] == "0", "The user can disable automatic relaunch.");
            window.Ready(true, scope, new Dictionary<string, string> { ["LaunchAfterSetup"] = "0" }, clientWasRunning: wasRunning);
            Require(window.Options()["LaunchAfterSetup"] == "0", "Explicit deferred-update launch suppression must take priority.");
            window.Ready(true, scope, new Dictionary<string, string> { ["LaunchAfterSetup"] = "1" });
            Require(window.Options()["LaunchAfterSetup"] == "1", "Explicit launch must work even if the client was stopped.");
        }
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Arguments()
    {
        new SetupArguments(new[] { "-norestart", "RegistrationOnly=1", "Scope=PerMachine" });
        var parsed = new SetupArguments(new[] { "Scope=PerMachine", "Mode=service", "BootUpdate=1", "LaunchAfterSetup=0" });
        Require(parsed.Boot && parsed.Scope == BundleScope.PerMachine, "Boot update options were not recognized.");
        parsed = new SetupArguments(new[] { "Scope=PerUser", "Mode=login", "StagedUpdate=1", "AutomaticUpdates=false" });
        Require(parsed.Scope == BundleScope.PerUser && parsed.Values["StagedUpdate"] == "1" && parsed.Values["AutomaticUpdates"] == "false", "Personal update options were lost.");
        Reject<ArgumentException>(() => new SetupArguments(new[] { "Scope=PerUser", "Mode=service", "BootUpdate=1" }));
        foreach (string argument in new[] { "Mode=unknown", "InstallSSH=0", "/TASKS=", "/MODE=service", "/BOOTUPDATE=1", "/UPDATE=1", "/CURRENTUSER", "/VERYSILENT" })
            Reject<ArgumentException>(() => new SetupArguments(new[] { argument }));
        Require(new SetupArguments(Array.Empty<string>()).Values.Count == 0, "Omitted preferences must remain unspecified.");
        foreach (string value in new[] { "", "a", @"C:\path with spaces\", @"C:\path\file.dll", "[value]" })
            Require(SetupArguments.Split(Program.Quote(value))[0] == value, "Windows argument quoting failed.");
        Reject<ArgumentException>(() => Program.Quote("a\"b"));
    }

    private static void Permissions()
    {
        var creator = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        Require(!Program.UntrustedWrite(new FileSystemAccessRule(creator, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow)), "Legacy inheritable creator-owner permissions must be accepted before hardening.");
        Require(Program.UntrustedWrite(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow)), "Unprivileged writes must be rejected.");
        Require(Program.UntrustedWrite(new FileSystemAccessRule(creator, FileSystemRights.FullControl, AccessControlType.Allow)), "A direct creator-owner grant must not bypass validation.");
        Require(!Program.UntrustedWrite(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow)), "Read-only user access must be accepted.");
        Require(!Program.UntrustedWrite(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), FileSystemRights.FullControl, AccessControlType.Allow)), "Windows TrustedInstaller is a privileged service.");
        var generic = new DirectorySecurity();
        generic.SetSecurityDescriptorSddlForm("D:(A;OIIO;GA;;;BU)");
        Require(Program.UntrustedWrite((FileSystemAccessRule)generic.GetAccessRules(true, false, typeof(SecurityIdentifier))[0]), "Generic write grants to unprivileged users must be rejected.");
    }

    private static void Archives(string root)
    {
        foreach (string invalid in new[] { "../outside", "C:/outside", "/absolute", "a\\b", "a/../b", "a//b", "./a", "", "NUL.dll", "CON", "a.", ".setup/state", "update/setup.exe", "user-installation.sid" })
            Reject<IOException>(() => Program.EntryPath(root, invalid));
        Require(Program.EntryPath(root, "folder/app.dll") == Path.Combine(root, "folder", "app.dll"), "Valid archive path rejected.");
        foreach (string[] entries in new[] { new[] { "../escape" }, new[] { "duplicate.dll", "DUPLICATE.dll" } })
        {
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) foreach (string name in entries) zip.CreateEntry(name);
            memory.Position = 0;
            using var archive = new ZipArchive(memory);
            Reject<IOException>(() => Program.ExtractPayload(archive, target));
        }
    }

    private static async Task Runtime(string root)
    {
        foreach (string url in new[] { "http://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/a.nupkg", "https://evil.test/a.nupkg", "https://api.nuget.org.evil.test/a.nupkg", "https://user@api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/a.nupkg" })
            Require(!Program.OfficialRuntime(new Uri(url)), "Untrusted runtime source accepted.");
        Require(Program.OfficialRuntime(new Uri("https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/10.0.14/microsoft.netcore.app.runtime.win-x64.10.0.14.nupkg")), "Official runtime source rejected.");
        string installed = Path.Combine(root, "installed"), staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(staging);
        byte[] bytes = { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(installed, "coreclr.dll"), bytes);
        using var hash = SHA256.Create();
        string checksum = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        File.WriteAllBytes(Path.Combine(installed, "WindowsMegaProxy.Service.exe"), bytes);
        var boot = new SetupManifest { Files = new[]
        {
            new RuntimeFile { Path = "WindowsMegaProxy.Service.exe", Sha256 = checksum },
            new RuntimeFile { Path = "launcher/ignored.dll", Sha256 = checksum }
        } };
        Require(Program.VerifyInstalledInventory(boot, installed).Length == 1, "System boot registration must ignore personal launcher files.");
        File.WriteAllText(Path.Combine(installed, "WindowsMegaProxy.Service.exe"), "incomplete");
        Reject<IOException>(() => Program.VerifyInstalledInventory(boot, installed));
        var field = typeof(Program).GetField("Root", BindingFlags.Static | BindingFlags.NonPublic)!;
        field.SetValue(null, installed);
        var manifest = new SetupManifest
        {
            Packages = new[] { new RuntimePackage { Url = "https://invalid.test/must-not-download" } },
            RuntimeFiles = new[] { new RuntimeFile { Path = "coreclr.dll", Sha256 = checksum, Package = 0 } }
        };
        var method = typeof(Program).GetMethod("PrepareRuntime", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(null, new object[] { manifest, staging })!;
        Require(Program.HashMatches(Path.Combine(staging, "coreclr.dll"), checksum), "A valid installed runtime was not reused.");
        File.Delete(Path.Combine(staging, "coreclr.dll"));
        File.WriteAllText(Path.Combine(installed, "coreclr.dll"), "damaged");
        try { await (Task)method.Invoke(null, new object[] { manifest, staging })!; throw new Exception("Damaged cache was accepted."); }
        catch (IOException) { }
        Require(!File.Exists(Path.Combine(staging, "coreclr.dll")), "Damaged runtime was copied into installation staging.");
    }

    private static void Capture(SetupWindow window, string path)
    {
        window.ChoicesView.UpdateLayout();
        using var bitmap = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
        // WM_PRINT does not render the WPF child of ElementHost.
        var host = window.Controls.OfType<System.Windows.Forms.Integration.ElementHost>().Single();
        var visual = (System.Windows.FrameworkElement)host.Child;
        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(host.Width, host.Height,
            96 * host.Width / visual.ActualWidth, 96 * host.Height / visual.ActualHeight, System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
        using var memory = new MemoryStream(); encoder.Save(memory); memory.Position = 0;
        using var content = Image.FromStream(memory);
        using var graphics = Graphics.FromImage(bitmap);
        var position = host.PointToScreen(Point.Empty);
        graphics.DrawImage(content, position.X - window.Left, position.Y - window.Top, host.Width, host.Height);
        bitmap.Save(path);
    }

    private static void Windows(string directory)
    {
        Require(SetupWindow.WheelDistance(-120, 3, 16, 400) == 48, "Wheel scrolling must honor Windows line count.");
        Require(SetupWindow.WheelDistance(30, 3, 16, 400) == -12, "High-resolution wheel deltas must retain their proportion and direction.");
        Require(SetupWindow.WheelDistance(-120, 0, 16, 400) == 0, "Disabled wheel scrolling must not move content.");
        Require(SetupWindow.WheelDistance(-120, -1, 16, 400) == 400, "Windows page scrolling must use the viewport height.");
        Directory.CreateDirectory(directory);
        Application.EnableVisualStyles();
        foreach (string language in new[] { "en", "ru" })
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo(language);
            Console.WriteLine("Creating wizard: " + language);
            using var window = new SetupWindow(null, "0.0.0");
            window.Ready(false, BundleScope.Default, new Dictionary<string, string>());
            Require(window.Scope == BundleScope.PerUser, "Fresh installations must default to the current user.");
            Require(window.Options()["Mode"] == "manual" && window.Options()["AutomaticUpdates"] == "true", "Fresh installations must have concrete defaults.");
            Require(window.Options()["RemoveData"] == "0", "Uninstall must keep data by default.");
            window.Ready(false, BundleScope.PerUser, new Dictionary<string, string> { ["Mode"] = "login" });
            Require(window.Scope == BundleScope.PerUser && window.Options()["Mode"] == "login", "Personal UI options were lost.");
            window.Show(); Application.DoEvents();
            Require(window.Step == -1 && window.LicenseAcceptance.IsChecked != true, "Licenses must require explicit acceptance.");
            window.Advance();
            Require(window.Step == -1, "Unaccepted licenses must prevent advancing.");
            Capture(window, Path.Combine(directory, language + "-license.png"));
            var licenseHost = window.Controls.OfType<System.Windows.Forms.Integration.ElementHost>().Single();
            var licensePanel = (System.Windows.Controls.DockPanel)licenseHost.Child;
            var licenseText = licensePanel.Children.OfType<System.Windows.Controls.TextBox>().Single();
            licenseText.ScrollToHome(); licenseText.UpdateLayout();
            var licenseWheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -30)
            {
                RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent
            };
            licenseText.RaiseEvent(licenseWheel); licenseText.UpdateLayout();
            double licenseDistance = Math.Min(licenseText.ExtentHeight - licenseText.ViewportHeight,
                SetupWindow.WheelDistance(-30, System.Windows.SystemParameters.WheelScrollLines,
                    licenseText.FontSize * licenseText.FontFamily.LineSpacing, licenseText.ViewportHeight));
            Require(licenseWheel.Handled && Math.Abs(licenseText.VerticalOffset - licenseDistance) < 0.01,
                "License scrolling must honor Windows settings without WPF acceleration.");
            licenseText.ScrollToHome();
            window.LicenseAcceptance.IsChecked = true;
            window.Advance();
            window.Previous();
            window.LicenseAcceptance.IsChecked = false;
            window.Advance();
            Require(window.Step == -1, "Revoking acceptance must prevent advancing.");
            window.LicenseAcceptance.IsChecked = true;
            window.Advance();
            window.ChoicesView.UpdateLayout();
            Require(window.ChoicesView.ScrollableHeight == 0, "The first wizard page must not scroll when its content fits.");
            var scopeChoices = (System.Windows.Controls.StackPanel)window.ChoicesView.Content;
            Require(((System.Windows.Controls.RadioButton)scopeChoices.Children[0]).IsChecked == true, "Personal scope must be the first choice.");
            for (int step = 0; step < 3; step++)
            {
                Require(window.Step == step, "Wizard navigation skipped a step.");
                Application.DoEvents();
                Capture(window, Path.Combine(directory, language + "-step-" + (step + 1) + ".png"));
                if (step < 2) window.Advance();
            }
            foreach (var toggle in new[] { window.DesktopSwitch, window.LaunchSwitch })
            {
                var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
                Require(!string.IsNullOrWhiteSpace(peer.GetName()), "Switches need accessible names.");
                ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            }
            Require(window.Options()["DesktopShortcut"] == "1" && window.Options()["LaunchAfterSetup"] == "1", "Switches must update installation options.");
            window.Select(2, "false"); window.Previous(); window.Previous();
            Require(window.Step == 0 && window.Options()["Mode"] == "login" && window.Options()["AutomaticUpdates"] == "false", "Going back must preserve choices.");
            window.Select(0, "machine"); window.Select(1, "service"); window.Select(0, "user");
            Require(window.Options()["Mode"] == "manual", "A fresh personal installation must clear service mode to manual.");
            Reject<ArgumentException>(() => window.Select(1, "service"));
            window.Ready(true, BundleScope.PerMachine, new Dictionary<string, string> { ["Mode"] = "service" });
            Require(window.Scope == BundleScope.PerMachine && window.Options()["Mode"] == "service", "Maintenance UI options were lost.");
            window.Advance();
            window.Advance(); Application.DoEvents();
            Capture(window, Path.Combine(directory, language + "-system-mode.png"));
            var large = window.Size;
            window.Size = window.MinimumSize;
            Application.DoEvents(); window.ChoicesView.UpdateLayout();
            Require(window.ChoicesView.ScrollableHeight > 0, "A short window must allow access to all choices.");
            var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -30)
            {
                RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent
            };
            window.ChoicesView.RaiseEvent(wheel);
            window.ChoicesView.UpdateLayout();
            double expected = Math.Min(window.ChoicesView.ScrollableHeight, SetupWindow.WheelDistance(-30,
                System.Windows.SystemParameters.WheelScrollLines,
                window.ChoicesView.FontSize * window.ChoicesView.FontFamily.LineSpacing, window.ChoicesView.ViewportHeight));
            Require(wheel.Handled && Math.Abs(window.ChoicesView.VerticalOffset - expected) < 0.01,
                "Wizard wheel input must apply the system setting once, without WPF acceleration.");
            window.ChoicesView.ScrollToEnd(); window.ChoicesView.UpdateLayout();
            Require(window.ChoicesView.VerticalOffset > 0, "The narrow scrollbar must support scrolling.");
            Capture(window, Path.Combine(directory, language + "-compact.png"));
            window.Size = large; window.Previous();
            Application.DoEvents(); window.ChoicesView.UpdateLayout();
            Require(window.ChoicesView.ScrollableHeight == 0 && window.ChoicesView.VerticalOffset == 0, "Returning to the first page must clear old scroll extent and offset.");
            window.LicenseAcceptance.IsChecked = false;
            window.Removal();
            window.Select(3, "1");
            Require(window.Options()["RemoveData"] == "1", "Full removal must require an explicit selection.");
            Application.DoEvents();
            Capture(window, Path.Combine(directory, language + "-uninstall.png"));
            window.Begin();
            window.AppendLog("Preparing runtime.\r\nOriginal installation error.\r\nRollback completed.\r\n");
            window.Progress(42);
            Require(window.InstallProgress.Value == 42 && !window.InstallProgress.IsIndeterminate, "Progress must show the reported percentage.");
            Require(window.InstallLog.IsReadOnly && window.InstallLog.Text.Contains("Original installation error"), "Installation log must remain selectable and read-only.");
            Application.DoEvents();
            Capture(window, Path.Combine(directory, language + "-progress.png"));
            window.AppendLog(new string('x', 150000));
            Require(window.InstallLog.Text.Length <= 131072, "Visible log must stay bounded.");
            window.Complete(1, "unused.log");
            Require(window.InstallProgress.Value == 42, "Failure must not reset progress and hide installation state.");
            window.Close();
        }
    }
}
