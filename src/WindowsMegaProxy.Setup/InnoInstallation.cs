using System;
using System.IO;
using Microsoft.Win32;

internal static class InnoInstallation
{
    internal const string Message = "An older Inno Setup installation was found. Export your configuration, uninstall the old version in Windows Settings, then run this installer again.";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{1D673B75-6671-42B4-97AC-99A85041D6C3}_is1";

    internal static bool Exists()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(UninstallKey);
                if (key is not null) return true;
            }
        foreach (string directory in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsMegaProxy"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsMegaProxy")
        })
            if (File.Exists(Path.Combine(directory, "unins000.exe")) || File.Exists(Path.Combine(directory, "unins000.dat"))) return true;
        return false;
    }
}
