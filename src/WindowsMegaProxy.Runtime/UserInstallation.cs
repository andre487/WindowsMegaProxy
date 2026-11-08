using System;
using System.IO;
using System.Security.Principal;

namespace WindowsMegaProxy;

public static class UserInstallation
{
    public const string Marker = "user-installation.sid";
    public static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    public static string RegistryPath => StartupConfiguration.RegistryPath + @"\UserInstallation";
    public static string TaskName => AppIdentity.Name + ".User." + Sid;
    // Keep personal profiles out of the legacy store that system installs migrate to global settings.
    public static string DataDirectory => Path.Combine(SettingsStore.DirectoryPath, "Personal");
    public static string ValidateMode(string mode) => StartupConfiguration.Validate(mode) == "service"
        ? throw new ArgumentException("Windows service mode is not available in a current-user installation.") : mode;
    public static string ProtectedRoot(string programFiles, string sid) =>
        Path.Combine(programFiles, AppIdentity.Name + ".Users", new SecurityIdentifier(sid).Value);

    public static string ValidateDirectory(string directory, string programFiles, string sid)
    {
        string root = ProtectedRoot(programFiles, sid);
        if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The user runtime must run from its protected installation directory.");
        return root;
    }

    public static bool IsUser
    {
        get
        {
            string marker = Path.Combine(AppContext.BaseDirectory, Marker);
            if (!File.Exists(marker)) return false;
            if (File.ReadAllText(marker).Trim() != Sid) throw new UnauthorizedAccessException("This installation belongs to another Windows user.");
            ValidateDirectory(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Sid);
            return true;
        }
    }

    public static void Launch()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        scheduler.GetFolder(@"\").GetTask(TaskName).Run(null);
    }
}
