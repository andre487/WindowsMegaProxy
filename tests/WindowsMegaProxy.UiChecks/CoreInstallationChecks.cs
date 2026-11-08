using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class CoreInstallationChecks
{
    internal static async Task RunAsync()
    {
        foreach (string version in new[] { "1.15.0-alpha.13", "1.15.0-alpha.14", "1.15.0", "1.15.1", "v1.16.0" })
            if (!CoreInstallation.SupportsHttp3(version)) throw new Exception("HTTP/3 core version rejected: " + version);
        foreach (string version in new[] { "1.14.4", "1.15.0-alpha.12", "1.15", "invalid", "1.15.0-unknown", "1.15.0-alpha.999999999999999" })
            if (CoreInstallation.SupportsHttp3(version)) throw new Exception("Unsupported HTTP/3 core version accepted: " + version);
        var profile = ProxyProfile.Create("HTTP/3 setting check");
        profile.Proxy["type"] = "MASQUE";
        profile.Proxy["host"] = "proxy.invalid";
        profile.Proxy["port"] = 443;
        var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" };
        if (settings.Http3Enabled) throw new Exception("HTTP/3 must be opt-in.");
        using var engine = new Engine();
        try { await engine.StartAsync(settings, testOnly: true); throw new Exception("Disabled HTTP/3 must not start a core."); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Enable HTTP/3", StringComparison.Ordinal)) { }
        settings.Http3Enabled = true;
        var exported = PortableConfiguration.Export(settings);
        var imported = PortableConfiguration.Import(exported.ToJsonString(), new Settings());
        if (!imported.Http3Enabled) throw new Exception("The Windows HTTP/3 option must survive config export/import.");
        var review = new ImportReview(ProfileTransfer.Parse(exported.ToJsonString()), new Settings());
        if (!review.EnablesHttp3) throw new Exception("Import must request confirmation before enabling HTTP/3.");
        var empty = new HashSet<string>();
        var declined = review.Apply(empty, true, empty);
        if (declined.Http3Enabled || declined.Profiles.Count != 1) throw new Exception("Declining HTTP/3 must still import profiles.");
        if (!review.Apply(empty, true, empty, allowHttp3Enable: true).Http3Enabled) throw new Exception("Confirmed HTTP/3 enable must be imported.");
        exported["windows"]!.AsObject().Remove("http3Enabled");
        if (PortableConfiguration.Import(exported.ToJsonString(), new Settings()).Http3Enabled) throw new Exception("Absent HTTP/3 option must keep the default off.");
        var existing = settings.Copy();
        var preserve = new ImportReview(ProfileTransfer.Parse(exported.ToJsonString()), existing);
        if (preserve.EnablesHttp3 || !preserve.Apply(empty, true, empty).Http3Enabled) throw new Exception("An absent option must preserve an existing enabled setting.");
        existing.Language = "ru"; existing.Theme = "light"; existing.AutoConnect = true;
        existing.BypassLan = false; existing.ShowAllConnections = true; existing.LogSizeMb = 17;
        existing.TlsFingerprint = "firefox"; existing.LaunchAtSignIn = true;
        existing.WindowsOptions[AppUpdatePolicy.Option] = false;
        exported["windows"] = new System.Text.Json.Nodes.JsonObject { ["theme"] = "dark" };
        var partial = PortableConfiguration.Import(exported.ToJsonString(), existing);
        if (!partial.Http3Enabled || partial.Language != "ru" || partial.Theme != "dark" || !partial.AutoConnect ||
            partial.BypassLan || !partial.ShowAllConnections || partial.LogSizeMb != 17 ||
            partial.TlsFingerprint != "firefox" || partial.LaunchAtSignIn != true || AppUpdatePolicy.Enabled(partial))
            throw new Exception("A partial Windows import must change only explicitly supplied options.");
    }
}
