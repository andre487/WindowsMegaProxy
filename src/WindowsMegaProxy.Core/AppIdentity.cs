namespace WindowsMegaProxy;

public static class AppIdentity
{
#if MEGAPROXY_DEV
    public static bool IsDev => true;
    public const string Name = "WindowsMegaProxy.Dev";
    public const string DisplayName = "MegaProxy DEV";
    public const string IconPrefix = "dev-";
#else
    public static bool IsDev => false;
    public const string Name = "WindowsMegaProxy";
    public const string DisplayName = "MegaProxy";
    public const string IconPrefix = "";
#endif
    public const string Executable = Name + ".exe";
    public const string ServiceExecutable = Name + ".Service.exe";
    public const string MutexName = @"Local\" + Name;
    public static string CoreExecutable => IsDev ? "WindowsMegaProxy.Dev.Core.exe" : "WindowsMegaProxy.Core.exe";
}
