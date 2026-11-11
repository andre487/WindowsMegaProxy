using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WixToolset.BootstrapperApplicationApi;

internal sealed class SetupArguments
{
    internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
    internal BundleScope Scope;
    internal bool Boot => Values.TryGetValue("BootUpdate", out var value) && value == "1";

    internal SetupArguments(IEnumerable<string> args)
    {
        foreach (string arg in args)
        {
            // Burn may forward its restart policy to the bootstrapper application.
            if (arg.Equals("-norestart", StringComparison.OrdinalIgnoreCase)) continue;
            string[] pair = arg.Split(new[] { '=' }, 2);
            if (pair.Length != 2) throw new ArgumentException("Unknown setup argument: " + arg);
            string name = pair[0], value = pair[1];
            if (name == "Scope") Scope = value switch
            {
                "PerUser" => BundleScope.PerUser,
                "PerMachine" => BundleScope.PerMachine,
                _ => throw new ArgumentException("Invalid installation scope.")
            };
            else { Validate(name, value); Values[name] = value; }
        }
        if (Boot && (Scope == BundleScope.PerUser || !Values.TryGetValue("Mode", out string mode) || mode != "service"))
            throw new ArgumentException("Boot updates require system service mode.");
    }

    internal static void Validate(string name, string value)
    {
        bool valid = name switch
        {
            "Mode" => value is "preserve" or "manual" or "login" or "service",
            "AutomaticUpdates" => value is "preserve" or "true" or "false",
            "RemoveData" or "RegistrationOnly" or "DesktopShortcut" or "LaunchAfterSetup" or "StagedUpdate" or "BootUpdate" => value is "0" or "1",
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
