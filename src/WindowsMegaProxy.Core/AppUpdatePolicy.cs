using System;

namespace WindowsMegaProxy;

public static class AppUpdatePolicy
{
    public const string Option = "automaticAppUpdates";
    public const string ProxyOption = "appUpdatesThroughProxy";
    private static bool Supported(Settings settings) => settings.WindowsOptions["version"] is null || settings.WindowsOptions["version"]!.GetValue<int>() == 1;
    public static bool ThroughProxy(Settings settings) => Supported(settings) && (settings.WindowsOptions[ProxyOption]?.GetValue<bool>() ?? false);
    public static bool Enabled(Settings settings) => !Supported(settings) || (settings.WindowsOptions[Option]?.GetValue<bool>() ?? true);
    public static bool CheckDue(DateTimeOffset lastCheck, DateTimeOffset now, int failedChecks = 0) => lastCheck == default ||
        lastCheck > now || now - lastCheck >= (failedChecks <= 0 ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Min(failedChecks - 1, 4)))));
    public static bool NotificationDue(string version, string? notified, DateTimeOffset lastNotification,
        string? acknowledged, DateTimeOffset now) => version != acknowledged &&
        (version != notified || lastNotification == default || now - lastNotification >= TimeSpan.FromDays(7));
}
