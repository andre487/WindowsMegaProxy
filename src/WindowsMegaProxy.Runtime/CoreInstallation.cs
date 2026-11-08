using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public static class CoreInstallation
{
    public const string WingetKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SagerNet.sing-box_Microsoft.Winget.Source_8wekyb3d8bbwe";
    public static string Http3Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "WindowsMegaProxy.Dependencies", "sing-box-h3", Configuration.Http3CoreVersion);

    public static bool SupportsHttp3(string version)
    {
        var match = Regex.Match(version, @"^v?(\d+\.\d+\.\d+)(?:-alpha\.(\d+))?$");
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
        int comparison = parsed.CompareTo(new Version(1, 15, 0));
        return comparison > 0 || comparison == 0 && (!match.Groups[2].Success ||
            int.TryParse(match.Groups[2].Value, out int alpha) && alpha >= 13);
    }

    public static (string Path, string Version)? Stable()
    {
        using var key = Registry.LocalMachine.OpenSubKey(WingetKey);
        if (key?.GetValue("InstallLocation") is not string location || key.GetValue("DisplayVersion") is not string version) return null;
        string root = Path.GetFullPath(location);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
        if (!root.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The machine-wide sing-box package must be installed under Program Files.");
        CheckLinks(root);
        string name = "sing-box-" + version.TrimStart('v') + "-windows-amd64";
        string executable = Path.Combine(root, name, "sing-box.exe");
        if (!File.Exists(executable)) executable = Path.Combine(root, "sing-box.exe");
        if (!File.Exists(executable)) return null;
        CheckLinks(executable);
        return (executable, version);
    }

    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Core installation paths cannot contain links.");
    }

    public static string Resolve(bool http3)
    {
        var stable = Stable();
        if (!http3 || stable is { } installed && SupportsHttp3(installed.Version))
            return stable?.Path ?? throw new FileNotFoundException("Install the machine-wide SagerNet.sing-box package with WinGet.");
        string executable = Path.Combine(Http3Directory, "sing-box.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("HTTP/3 requires an additional sing-box download. Connect from the interface to approve it.");
        CheckLinks(executable);
        return executable;
    }
}
