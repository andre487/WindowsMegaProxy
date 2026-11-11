using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using WindowsMegaProxy;

internal static class NativeCoreChecks
{
    public static void Run()
    {
        string? previous = Environment.GetEnvironmentVariable("WINDOWSMEGAPROXY_CORE");
        try
        {
            Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", "untrusted.exe");
            using var engine = new Engine();
            string actual = (string)typeof(Engine).GetMethod("CoreExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, [false])!;
            if (actual != Path.Combine(AppContext.BaseDirectory, "dependencies", "sing-box", AppIdentity.CoreExecutable))
                throw new Exception("The default engine allowed an environment override of the production core.");
            foreach (string legacy in new[] { "sing-box.exe", "sing-box-dev.exe" })
            {
                Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", legacy);
                using var dev = new Engine(allowEnvironmentOverrides: true);
                try
                {
                    typeof(Engine).GetMethod("CoreExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dev, [false]);
                    throw new Exception("DEV accepted a legacy core executable.");
                }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
            }
        }
        finally { Environment.SetEnvironmentVariable("WINDOWSMEGAPROXY_CORE", previous); }
        string root = Path.GetFullPath(Path.Combine("artifacts/native-cleanup-checks", Guid.NewGuid().ToString("N")));
        var cleanup = typeof(Installation).GetMethod("RemoveLegacyCoreFiles", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            foreach (string folder in new[] { "sing-box", "sing-box-h3" })
            {
                string directory = Path.Combine(root, "dependencies", folder);
                Directory.CreateDirectory(directory);
                if (StartupConfiguration.IsElevated)
                    foreach (string path in new[] { Path.Combine(root, "dependencies"), directory })
                    {
                        var info = new DirectoryInfo(path);
                        var security = info.GetAccessControl();
                        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                        info.SetAccessControl(security);
                    }
                string[] legacy = ["sing-box.exe", "sing-box-dev.exe", "libcronet.dll"];
                foreach (string file in legacy) File.WriteAllText(Path.Combine(directory, file), "old");
                File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
                cleanup.Invoke(null, [root]);
                foreach (string file in legacy)
                    if (!File.Exists(Path.Combine(directory, file))) throw new Exception("An incomplete core upgrade deleted old dependencies.");
                string host = Path.Combine(directory, AppIdentity.CoreExecutable);
                File.WriteAllText(host, "new host");
                cleanup.Invoke(null, [root]);
                if (!File.Exists(Path.Combine(directory, legacy[0]))) throw new Exception("A missing DLL must prevent legacy cleanup.");
                string dll = Path.Combine(directory, folder == "sing-box" ? "MegaProxy.Core.Stable.dll" : "MegaProxy.Core.Experimental.dll");
                File.WriteAllText(dll, "new DLL");
                if (!StartupConfiguration.IsElevated)
                {
                    try { cleanup.Invoke(null, [root]); throw new Exception("Cleanup accepted an untrusted directory owner."); }
                    catch (TargetInvocationException error) when (error.InnerException is UnauthorizedAccessException) { }
                    foreach (string file in legacy)
                        if (!File.Exists(Path.Combine(directory, file))) throw new Exception("Untrusted cleanup removed files.");
                    return;
                }
                cleanup.Invoke(null, [root]);
                foreach (string file in legacy)
                    if (File.Exists(Path.Combine(directory, file))) throw new Exception("Obsolete core binary remained after upgrade.");
                if (!File.Exists(host) || !File.Exists(dll) || !File.Exists(Path.Combine(directory, "keep.txt")))
                    throw new Exception("Core upgrade cleanup removed unrelated files.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
