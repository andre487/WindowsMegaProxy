using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using WindowsMegaProxy;

internal static class PublishedUpdateChecks
{
    private static void RequireRunner()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("Published update tests require a disposable GitHub runner.");
    }

    public static void Settings(string action)
    {
        RequireRunner();
        var store = new MachineSettingsStore();
        if (action == "seed")
        {
            var profile = ProxyProfile.Create("Automatic update preservation check");
            profile.Proxy["host"] = "proxy.invalid";
            profile.Proxy["password"] = "synthetic-update-secret";
            store.Save(new Settings { Profiles = [profile], ActiveProfileId = profile.Id, AutoConnect = false, Language = "en" });
        }
        else if (action != "verify") throw new ArgumentException("Unknown preservation action.");
        var settings = store.Load();
        if (settings.AutoConnect || !AppUpdatePolicy.Enabled(settings) || settings.Profiles.Count != 1 ||
            settings.ActiveProfile?.Name != "Automatic update preservation check" || settings.ActiveProfile.Proxy["password"]?.ToString() != "synthetic-update-secret")
            throw new Exception("Automatic update settings or the encrypted profile were not preserved.");
        if (File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")).Contains("synthetic-update-secret", StringComparison.Ordinal))
            throw new Exception("The update fixture secret must remain encrypted.");
    }

    public static void Run(string version)
    {
        RequireRunner();
        _ = AppUpdateTrust.ParseVersion(version);
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsMegaProxy", "WindowsMegaProxy.exe");
        using var original = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
        T Wait<T>(Func<T?> read, string description) where T : class
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(5))
            {
                try { if (read() is { } result) return result; }
                catch (ElementNotAvailableException) { }
                Thread.Sleep(250);
            }
            throw new TimeoutException(description);
        }
        AutomationElement Root(Process process) => AutomationElement.FromHandle(process.MainWindowHandle);
        AutomationElement? Find(AutomationElement root, string id)
        {
            var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            return element is not null && element.Current.IsEnabled && !element.Current.IsOffscreen ? element : null;
        }
        var window = Wait(() => { original.Refresh(); return original.MainWindowHandle == IntPtr.Zero ? null : Root(original); }, "The old client did not open.");
        var settingsTab = Wait(() => Find(window, "SettingsTab"), "Settings navigation is unavailable.");
        ((SelectionItemPattern)settingsTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        var updates = Wait(() => Find(window, "UpdatesCategoryButton"), "Updates category is unavailable.");
        ((InvokePattern)updates.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        // Do not click Check now: the installed client must check and download automatically.
        var install = Wait(() => Find(window, "InstallUpdateButton") is { } button && button.Current.IsEnabled && !button.Current.IsOffscreen ? button : null,
            "The old client did not automatically download the published update.");
        if (Find(window, "UpdateVersionText")?.Current.Name.Contains(version, StringComparison.Ordinal) != true)
            throw new Exception("The client downloaded an unexpected update version.");
        Console.WriteLine("The installed 0.0.7 client automatically downloaded and verified update " + version + ".");
        ((InvokePattern)install.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        if (!original.WaitForExit(30000)) throw new Exception("The old UI did not release its installation files.");
        using var reopened = Wait(() => Process.GetProcessesByName("WindowsMegaProxy").FirstOrDefault(process =>
            process.Id != original.Id && process.MainWindowHandle != IntPtr.Zero &&
            FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0] == version), "Setup did not reopen the updated client.");
        var updatedWindow = Root(reopened);
        var updatedSettings = Wait(() => Find(updatedWindow, "SettingsTab"), "The updated client did not respond.");
        ((SelectionItemPattern)updatedSettings.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        var updatedCategory = Wait(() => Find(updatedWindow, "UpdatesCategoryButton"), "The updated client did not expose its settings.");
        ((InvokePattern)updatedCategory.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Wait(() => Find(updatedWindow, "InstalledVersionText") is { } text && text.Current.Name.Contains(version, StringComparison.Ordinal) ? text : null,
            "The updated UI reports an incorrect version.");
        Console.WriteLine("The Install Update button completed installation and reopened version " + version + ".");
    }
}
