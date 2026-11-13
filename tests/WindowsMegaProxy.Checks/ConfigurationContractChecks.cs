using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

internal static class ConfigurationContractChecks
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var profile = ProxyProfile.Create("Contract"); profile.Proxy["host"] = "proxy.example";
        var original = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy", TunMtu = 1400, TrafficUnits = "SI" };
        Check(original.BlockDisabledIpv6, "IPv6 blocking is opt-out.");
        original.BlockDisabledIpv6 = false;
        var blockDocument = PortableConfiguration.Export(original);
        Check(!PortableConfiguration.Import(blockDocument.ToJsonString(), new Settings()).BlockDisabledIpv6, "IPv6 blocking preference round-trips.");
        blockDocument["windows"]!.AsObject().Remove("blockDisabledIpv6");
        Check(!PortableConfiguration.Import(blockDocument.ToJsonString(), original).BlockDisabledIpv6, "Omitted blocking preference is preserved.");
        original.BlockDisabledIpv6 = true;
        foreach (string route in new[] { "proxy", "system", "mixed" })
        foreach (bool profileIpv6 in new[] { false, true })
        foreach (bool systemIpv6 in new[] { false, true })
        foreach (bool block in new[] { false, true })
        {
            var mode = original.Copy();
            mode.ActiveProfile!.Data["routing"] = new JsonObject { ["allowIpv6"] = profileIpv6 };
            mode.SystemAllowIpv6 = systemIpv6;
            mode.BlockDisabledIpv6 = block;
            mode.DefaultAction = route == "proxy" ? "proxy" : "system";
            mode.Rules.Clear();
            if (route == "mixed") mode.Rules.Add(new RoutingRule { Source = "exe", Path = @"C:\Test.exe", Action = "proxy" });
            bool enabled = route == "proxy" ? profileIpv6 : route == "system" ? systemIpv6 : profileIpv6 && systemIpv6;
            var network = Configuration.Build(mode, 1, "test");
            string label = $"{route}: profile={profileIpv6}, system={systemIpv6}, block={block}";
            Check(network["inbounds"]![0]!["address"]!.AsArray().Count == (enabled || !block ? 2 : 1), label + " TUN address policy");
            Check(network["dns"]!["strategy"]!.ToString() == (enabled ? "prefer_ipv4" : "ipv4_only"), label + " DNS policy");
            Check(network["route"]!["rules"]!.AsArray().Any(rule => rule?["ip_version"]?.ToString() == "6" && rule?["action"]?.ToString() == "reject") == !enabled, label + " IPv6 rejection");
            Check(network["inbounds"]![0]!["strict_route"]!.GetValue<bool>() && network["inbounds"]![0]!["dns_mode"]!.ToString() == "hijack", label + " DNS protection remains active");
        }
        original.SystemAllowIpv6 = true;
        var ipv6Document = PortableConfiguration.Export(original);
        Check(ipv6Document["routing"]!["systemAllowIpv6"]!.GetValue<bool>(), "Export global IPv6 permission.");
        Check(PortableConfiguration.Import(ipv6Document.ToJsonString(), new Settings()).SystemAllowIpv6, "Import global IPv6 permission.");
        ipv6Document["routing"]!.AsObject().Remove("systemAllowIpv6");
        Check(PortableConfiguration.Import(ipv6Document.ToJsonString(), original).SystemAllowIpv6, "Omitted global IPv6 permission is preserved.");
        ipv6Document["routing"]!["systemAllowIpv6"] = false;
        Check(!PortableConfiguration.Import(ipv6Document.ToJsonString(), original).SystemAllowIpv6, "Explicit false replaces global IPv6 permission.");
        original.SystemAllowIpv6 = false;
        var document = PortableConfiguration.Export(original);
        foreach (var (portable, native) in new[] { ("CHROME_ANDROID", "chrome"), ("FIREFOX_ANDROID", "firefox"), ("EDGE_ANDROID", "edge"), ("RANDOMIZED", "randomized") })
        {
            document["tls"]!["fingerprint"] = portable;
            var settings = PortableConfiguration.Import(document.ToJsonString(), new Settings());
            Check(Configuration.Build(settings, 1, "test")["outbounds"]![0]!["tls"]!["utls"]!["fingerprint"]!.ToString() == native, "Imported supported fingerprint reaches the core.");
        }
        foreach (string unsupported in new[] { "CUSTOM", "SAMSUNG_INTERNET", "YANDEX_BROWSER", "future" })
        {
            document["tls"]!["fingerprint"] = unsupported;
            var review = new ImportReview(ProfileTransfer.Parse(document.ToJsonString()), original);
            Check(review.UnsupportedOptions.Contains(ConfigurationFields.FingerprintWarning), "Unsupported fingerprints produce an explicit notice.");
            Check(review.Apply(new HashSet<string>(), true, new HashSet<string>()).TlsFingerprint == "DEFAULT", "Unsupported fingerprints reset instead of retaining an old preset.");
        }
        document["tls"]!["fingerprint"] = "FIREFOX_ANDROID";
        document["windows"]!["tlsFingerprint"] = "chrome";
        document["windows"]!["logSizeMb"] = 99;
        document["diagnosticLogLimitMb"] = 7;
        var imported = PortableConfiguration.Import(document.ToJsonString(), new Settings());
        Check(imported.TlsFingerprint == "FIREFOX_ANDROID" && imported.LogSizeMb == 7, "Common values take precedence over legacy Windows aliases.");
        Check(imported.TunMtu == 1400 && imported.TrafficUnits == "SI", "MTU and traffic units survive transfer.");
        imported.TlsFingerprint = "EDGE_ANDROID"; imported.BypassLan = false; imported.LogSizeMb = 12;
        var exported = PortableConfiguration.Export(imported);
        Check(exported["tls"]!["fingerprint"]!.ToString() == "EDGE_ANDROID" && exported["windows"]!["tlsFingerprint"] is null && exported["diagnosticLogLimitMb"]!.GetValue<int>() == 12 && !exported["routing"]!["bypassLocalNetworks"]!.GetValue<bool>(), "Export writes current common values without stale aliases.");
        var unchanged = new ImportReview(ProfileTransfer.Parse(document.ToJsonString()), new Settings { Profiles = [profile], ActiveProfileId = profile.Id })
            .Apply(new HashSet<string>(), false, new HashSet<string>());
        Check(unchanged.TunMtu == 1500 && unchanged.TrafficUnits == "IEC", "Declining global import preserves local MTU and units.");
        document["windows"]!["version"] = 2;
        document["windows"]!["appUpdatesThroughProxy"] = true;
        document["windows"]!["automaticAppUpdates"] = false;
        imported = PortableConfiguration.Import(document.ToJsonString(), new Settings());
        Check(AppUpdatePolicy.Enabled(imported) && !AppUpdatePolicy.ThroughProxy(imported), "Unsupported Windows section cannot execute update settings.");
        document["profiles"]![0]!["routing"] = new JsonObject { ["allowIpv6"] = false };
        document["profiles"]![0]!["proxy"] = new JsonObject { ["type"] = "SSH", ["host"] = "proxy.example", ["port"] = 22, ["username"] = "test", ["password"] = "synthetic", ["trustedHostKey"] = "SHA256:example", ["acceptAnyHostKey"] = false };
        document["ssh"] = new JsonObject { ["fingerprint"] = "CONNECTBOT" };
        imported = PortableConfiguration.Import(document.ToJsonString(), new Settings { DefaultAction = "proxy" });
        var config = Configuration.Build(imported, 1, "test");
        Check(config["outbounds"]![0]!["client_version"]!.ToString() == "SSH-2.0-ConnectBot" && config["outbounds"]![0]!["host_key"] is null, "Shared SSH fingerprint applies without changing host-key policy.");
        Check(!imported.ActiveProfile!.AllowIpv6 && config["route"]!["rules"]!.AsArray().Any(rule => rule?["ip_version"] is not null), "Imported IPv6 flag controls IPv6.");
        document["subscription"] = new JsonObject { ["url"] = "https://feed.example/", ["accessToken"] = "synthetic" };
        var reviewUnknown = new ImportReview(ProfileTransfer.Parse(document.ToJsonString()), original);
        Check(reviewUnknown.UnsupportedOptions.Contains(ConfigurationFields.UnknownWarning), "Unknown fields warn without exposing their values.");
        Check(PortableConfiguration.Export(PortableConfiguration.Import(document.ToJsonString(), new Settings()))["subscription"]!["accessToken"] is null, "Unknown tokens are not re-exported.");
        ConfigurationSubscription.Configure(original, new SubscriptionDefinition("https://feed.example/", [], null, null, 60, true));
        original.DefaultAction = "system"; original.ActiveProfileId = null;
        var feed = PortableConfiguration.Export(original);
        feed["activeProfileId"] = profile.Id;
        feed["windows"]!["defaultAction"] = "proxy";
        feed["windows"]!["autoConnect"] = true;
        feed["windows"]!["launchAtSignIn"] = true;
        feed["tls"]!["fingerprint"] = "CUSTOM";
        var refreshed = ConfigurationSubscription.ApplySnapshot(original, ProfileTransfer.Parse(feed.ToJsonString()), 0, DateTimeOffset.UtcNow);
        Check(refreshed.DefaultAction == "system" && refreshed.ActiveProfileId is null && !refreshed.AutoConnect && refreshed.LaunchAtSignIn is null, "Subscription cannot change local connection intent or startup.");
        Check(ConfigurationSubscription.Status(refreshed) == "Updated with warnings" && refreshed.TlsFingerprint == "DEFAULT", "Subscription records normalization warnings.");
        var beforeSsh = SettingsAnalysis.Prepare(imported);
        imported.SharedOptions["ssh"]!["fingerprint"] = "JUICESSH";
        Check(SettingsAnalysis.Prepare(imported, beforeSsh).Signature != beforeSsh.Signature, "Global SSH fingerprint changes invalidate network analysis.");
        Console.WriteLine("Shared configuration contract checks passed.");
    }
}
