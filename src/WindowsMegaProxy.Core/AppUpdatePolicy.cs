using System;

namespace WindowsMegaProxy;

public static class AppUpdatePolicy
{
    public const string Option = "automaticAppUpdates";
    public const string ProxyOption = "appUpdatesThroughProxy";
    public static bool ThroughProxy(Settings settings) => settings.WindowsOptions[ProxyOption]?.GetValue<bool>() ?? false;
    public static bool Enabled(Settings settings) => settings.WindowsOptions[Option]?.GetValue<bool>() ?? true;
    public static bool CheckDue(DateTimeOffset lastCheck, DateTimeOffset now) => lastCheck == default ||
        lastCheck > now || now - lastCheck >= TimeSpan.FromDays(1);
    public static bool NotificationDue(string version, string? notified, DateTimeOffset lastNotification,
        string? acknowledged, DateTimeOffset now) => version != acknowledged &&
        (version != notified || lastNotification == default || now - lastNotification >= TimeSpan.FromDays(7));
}
