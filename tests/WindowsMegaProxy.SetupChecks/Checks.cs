using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using WixToolset.BootstrapperApplicationApi;

internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "MegaProxy.Setup.Check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Arguments();
            Archives(root);
            Runtime(root).GetAwaiter().GetResult();
            if (args.Length == 1) Windows(args[0]);
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

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Arguments()
    {
        new SetupArguments(new[] { "-norestart", "RegistrationOnly=1", "Scope=PerMachine" });
        var parsed = new SetupArguments(new[] { "/ALLUSERS", "/VERYSILENT", "/MODE=service", "/BOOTUPDATE=1", "/REOPEN=0" });
        Require(parsed.Boot && parsed.Silent && parsed.Scope == BundleScope.PerMachine, "Released boot worker switches were not recognized.");
        parsed = new SetupArguments(new[] { "/CURRENTUSER", "/SILENT", "/MODE=login", "/STAGEDUPDATE=1", "/UPDATE=1", "/MERGETASKS=!automaticupdates" });
        Require(parsed.Scope == BundleScope.PerUser && parsed.Values["StagedUpdate"] == "1" && parsed.Values["AutomaticUpdates"] == "false", "Released personal update switches were not recognized.");
        parsed = new SetupArguments(new[] { "Scope=PerUser", "Mode=manual", "AutomaticUpdates=true", "/TASKS=" });
        Require(parsed.Values["AutomaticUpdates"] == "false", "Explicit task clearing was lost.");
        Reject<ArgumentException>(() => new SetupArguments(new[] { "/CURRENTUSER", "/MODE=service", "/BOOTUPDATE=1" }));
        Reject<ArgumentException>(() => new SetupArguments(new[] { "Mode=unknown" }));
        Reject<ArgumentException>(() => new SetupArguments(new[] { "InstallSSH=0" }));
        Reject<ArgumentException>(() => new SetupArguments(new[] { "/TASKS=unknown" }));
        Require(new SetupArguments(new[] { "/UPDATE=1" }).Values["LaunchAfterSetup"] == "1", "Legacy updates must reopen the application by default.");
        Require(new SetupArguments(new[] { "/REOPEN=0", "/UPDATE=1" }).Values["LaunchAfterSetup"] == "0", "Explicit reopen suppression must survive argument ordering.");
        foreach (string value in new[] { "", "a", @"C:\path with spaces\", @"C:\path\file.dll", "[value]" })
            Require(SetupArguments.Split(Program.Quote(value))[0] == value, "Windows argument quoting failed.");
        Reject<ArgumentException>(() => Program.Quote("a\"b"));
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

    private static void Windows(string directory)
    {
        Directory.CreateDirectory(directory);
        Application.EnableVisualStyles();
        foreach (string language in new[] { "en", "ru" })
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = new CultureInfo(language);
            using var window = new SetupWindow(null, "0.0.0");
            window.Ready(false, BundleScope.PerUser, new Dictionary<string, string> { ["Mode"] = "login" });
            Require(window.Scope == BundleScope.PerUser && window.Options()["Mode"] == "login", "Personal UI options were lost.");
            window.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, window.ClientRectangle);
            bitmap.Save(Path.Combine(directory, language + ".png"));
            window.Ready(true, BundleScope.PerMachine, new Dictionary<string, string> { ["Mode"] = "service" });
            Require(window.Scope == BundleScope.PerMachine && window.Options()["Mode"] == "service", "Maintenance UI options were lost.");
            window.Close();
        }
    }
}
