using System;
using System.IO;
using System.Reflection;
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
    }
}
