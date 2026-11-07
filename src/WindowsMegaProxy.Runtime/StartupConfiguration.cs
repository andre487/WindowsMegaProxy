using System;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;
using System.Diagnostics;

namespace WindowsMegaProxy;

public static class StartupConfiguration
{
    public static string RegistryPath => @"SOFTWARE\" + AppIdentity.Name;
    public static string Validate(string mode) => mode is "manual" or "login" or "service" ? mode : throw new ArgumentException("Unknown operating mode.");
    public static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static ProcessStartInfo ModeCommand(string applicationDirectory, string? installScript, string mode)
    {
        Validate(mode);
        bool deploy = AppIdentity.IsDev && installScript is not null;
        string root = deploy ? Path.GetDirectoryName(installScript!)! : applicationDirectory;
        var start = new ProcessStartInfo(deploy ? Path.Combine(root, "scripts", ".venv", "Scripts", "python.exe") : Path.Combine(root, AppIdentity.ServiceExecutable))
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in deploy ? new[] { Path.Combine(root, "scripts", "install.py"), "--dev", "--launch", "--mode", mode, "--elevated" } : new[] { "--set-mode", mode })
            start.ArgumentList.Add(argument);
        return start;
    }
    public static string Mode
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key?.GetValue("Mode") is string value) return Validate(value);
            using var legacy = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name);
            return legacy is null ? "manual" : "service";
        }
    }

    public static Settings LoadLocal()
    {
        var store = new MachineSettingsStore();
        if (AppIdentity.IsDev && File.Exists(Path.Combine(SettingsStore.DirectoryPath, "settings.json")) &&
            (!store.Initialized || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(store.DirectoryPath, "settings.json")))?["scope"]?.GetValue<string>() != "machine"))
            store.Save(SettingsStore.Load());
        if (store.Initialized) return store.Load();
        var settings = SettingsStore.Load();
        store.Save(settings);
        return settings;
    }

    public static void SaveLocal(Settings settings)
    {
        new MachineSettingsStore().Save(settings);
    }

    public static IDisposable ClaimRuntime(string? path = null)
    {
        path ??= Path.Combine(new MachineSettingsStore().DirectoryPath, "host.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The host lock cannot be a reparse point.");
        // A file-share lock spans sessions and is released by Windows when its host crashes.
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another MegaProxy host is already running. Close it or stop its service first.", error); }
    }
}
