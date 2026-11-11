using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using WixToolset.BootstrapperApplicationApi;

internal sealed class SetupArguments
{
    internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    internal BundleScope Scope;
    internal bool Silent, Boot;
    internal string? LogPath, Directory;

    internal SetupArguments(IEnumerable<string> args)
    {
        bool update = false;
        foreach (string arg in args)
        {
            string[] pair = arg.Split(new[] { '=' }, 2);
            string name = pair[0], value = pair.Length == 2 ? pair[1] : "";
            if (name.StartsWith("-", StringComparison.Ordinal)) name = "/" + name.Substring(1);
            if (!name.StartsWith("/", StringComparison.Ordinal))
            {
                if (name == "Scope") Scope = value switch
                {
                    "PerUser" => BundleScope.PerUser,
                    "PerMachine" => BundleScope.PerMachine,
                    _ => throw new ArgumentException("Invalid installation scope.")
                };
                else { Validate(name, value); Values[name] = value; }
                continue;
            }
            // Released Inno clients pass these switches to the downloaded replacement installer.
            switch (name.ToUpperInvariant())
            {
                case "/VERYSILENT": case "/SILENT": Silent = true; break;
                case "/SUPPRESSMSGBOXES": case "/NORESTART": break;
                case "/ALLUSERS": Scope = BundleScope.PerMachine; break;
                case "/CURRENTUSER": Scope = BundleScope.PerUser; break;
                case "/MODE": Validate("Mode", value); Values["Mode"] = value; break;
                case "/STAGEDUPDATE": Validate("StagedUpdate", value); Values["StagedUpdate"] = value; break;
                case "/BOOTUPDATE": Validate("BootUpdate", value); Boot = value == "1"; break;
                case "/UPDATE": Validate("Update", value); update = value == "1"; break;
                case "/REOPEN": Validate("LaunchAfterSetup", value); Values["LaunchAfterSetup"] = value; break;
                case "/TASKS": case "/MERGETASKS":
                    if (name.Equals("/TASKS", StringComparison.OrdinalIgnoreCase))
                    { Values["AutomaticUpdates"] = "false"; Values["DesktopShortcut"] = "0"; }
                    foreach (string task in value.Split(',').Where(task => task.Length != 0))
                    {
                        switch (task.ToLowerInvariant())
                        {
                            case "automaticupdates": Values["AutomaticUpdates"] = "true"; break;
                            case "!automaticupdates": Values["AutomaticUpdates"] = "false"; break;
                            case "desktopicon": Values["DesktopShortcut"] = "1"; break;
                            case "!desktopicon": Values["DesktopShortcut"] = "0"; break;
                            default: throw new ArgumentException("Unknown setup task.");
                        }
                    }
                    break;
                case "/LOG": LogPath = Path.GetFullPath(value); break;
                case "/DIR": Directory = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown setup argument: " + name);
            }
        }
        if (update && !Values.ContainsKey("LaunchAfterSetup")) Values["LaunchAfterSetup"] = "1";
        if (Boot && (Scope == BundleScope.PerUser || !Values.TryGetValue("Mode", out string mode) || mode != "service"))
            throw new ArgumentException("Boot updates require system service mode.");
    }

    internal static void Validate(string name, string value)
    {
        bool valid = name switch
        {
            "Mode" => value is "preserve" or "manual" or "login" or "service",
            "AutomaticUpdates" => value is "preserve" or "true" or "false",
            "RegistrationOnly" or "DesktopShortcut" or "LaunchAfterSetup" or "StagedUpdate" or "BootUpdate" or "Update" => value is "0" or "1",
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid setup option: " + name);
    }

    internal static string[] Split(string commandLine)
    {
        IntPtr pointer = CommandLineToArgvW("setup " + commandLine, out int count);
        if (pointer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
