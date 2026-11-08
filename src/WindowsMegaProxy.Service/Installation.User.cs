using System;
using System.IO;
using Microsoft.Win32;
using WindowsMegaProxy;

public static partial class Installation
{
    private static int RunUser(string[] args)
    {
        if (!StartupConfiguration.IsElevated) throw new UnauthorizedAccessException("User installation requires administrator approval.");
        string command = args[0];
        bool uninstall = command is "--prepare-uninstall" or "--remove-installation";
        if (uninstall ? args.Length != 1 : command is not ("--configure-user" or "--set-mode"))
            throw new ArgumentException("Invalid user installation arguments.");
        bool? automaticUpdates = uninstall ? null : ReadAutomaticUpdates(args);
        string mode = uninstall ? "manual" : UserInstallation.ValidateMode(args[1]);
        string root = UserInstallation.ValidateDirectory(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), UserInstallation.Sid);
        foreach (string path in new[] { Path.GetDirectoryName(root)!, root, Path.Combine(root, "versions"), AppContext.BaseDirectory }) CheckDirectory(path);
        CloseClients(root, currentSession: !uninstall);
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        string name = UserInstallation.TaskName;
        string? launch = TaskXml(folder, name), login = TaskXml(folder, name + ".Login");
        Settings? previousOptions = null;
        try
        {
            if (uninstall)
            {
                DeleteTask(folder, name + ".Login");
                DeleteTask(folder, name);
                if (command == "--prepare-uninstall") Registry.CurrentUser.DeleteSubKeyTree(UserInstallation.RegistryPath, false);
                else
                {
                    using var registration = Registry.CurrentUser.OpenSubKey(UserInstallation.RegistryPath, true);
                    registration?.DeleteValue("Mode", false);
                    registration?.DeleteValue("HelperPath", false);
                }
                return 0;
            }
            SettingsStore.WriteAtomic(Path.Combine(AppContext.BaseDirectory, UserInstallation.Marker), UserInstallation.Sid);
            previousOptions = ConfigureAutomaticUpdates(automaticUpdates, personal: true);
            RegisterUserTask(scheduler, folder, name, false);
            if (mode == "login") RegisterUserTask(scheduler, folder, name + ".Login", true);
            else DeleteTask(folder, name + ".Login");
            using var key = Registry.CurrentUser.CreateSubKey(UserInstallation.RegistryPath);
            key.SetValue("Mode", mode);
            key.SetValue("HelperPath", Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable));
        }
        catch
        {
            if (previousOptions is not null) new MachineSettingsStore(UserInstallation.DataDirectory, machine: false).Save(previousOptions);
            RestoreTask(folder, name, launch);
            RestoreTask(folder, name + ".Login", login);
            throw;
        }
        if (command == "--set-mode") UserInstallation.Launch();
        return 0;
    }

    private static string? TaskXml(dynamic folder, string name)
    {
        try { return folder.GetTask(name).Xml; }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private static void DeleteTask(dynamic folder, string name)
    {
        if (TaskXml(folder, name) is not null) folder.DeleteTask(name, 0);
    }

    private static string UserTaskSecurity => "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + UserInstallation.Sid + ")";

    private static void RestoreTask(dynamic folder, string name, string? xml)
    {
        if (xml is null) DeleteTask(folder, name);
        else folder.RegisterTask(name, xml, 6, UserInstallation.Sid, null, 3, UserTaskSecurity);
    }

    private static void RegisterUserTask(dynamic scheduler, dynamic folder, string name, bool login)
    {
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "Run this user's protected MegaProxy installation with administrator privileges.";
        task.Principal.UserId = UserInstallation.Sid;
        task.Principal.LogonType = 3;
        task.Principal.RunLevel = 1;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        // A mode-switch helper may still belong to the old scheduled process tree during relaunch.
        task.Settings.MultipleInstances = 0;
        if (login)
        {
            dynamic trigger = task.Triggers.Create(9);
            trigger.UserId = UserInstallation.Sid;
        }
        dynamic action = task.Actions.Create(0);
        action.Path = Path.Combine(AppContext.BaseDirectory, AppIdentity.Executable);
        action.Arguments = login ? "--tray" : "";
        action.WorkingDirectory = AppContext.BaseDirectory;
        folder.RegisterTaskDefinition(name, task, 6, UserInstallation.Sid, null, 3, UserTaskSecurity);
    }
}
