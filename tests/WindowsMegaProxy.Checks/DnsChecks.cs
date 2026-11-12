using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

internal static class DnsChecks
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject(Action action)
        {
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; }
            throw new Exception("Invalid DNS configuration was accepted.");
        }
        var profile = ProxyProfile.Create("DNS test"); profile.Proxy["host"] = "proxy.example";
        var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" };
        string initial = Configuration.Signature(settings);
        var oldAnalysis = SettingsAnalysis.Prepare(settings);
        Check(ProfileDns.Provider(profile) == "CLOUDFLARE", "Old profiles retain Cloudflare.");
        Directory.CreateDirectory("artifacts/dns-checks");
        foreach (var provider in ProfileDns.Providers.Where(item => item.Id != "CUSTOM"))
        {
            profile.Data["dns"] = new JsonObject { ["provider"] = provider.Id, ["future"] = "preserve" };
            var config = Configuration.Build(settings, 9999, "test");
            var dns = config["dns"]!["servers"]![1]!;
            Check(dns["server"]!.ToString() == new Uri(provider.Url).Host && dns["detour"]!.ToString() == "proxy", "The selected provider uses the proxy.");
            Check(config["dns"]!["servers"]!.AsArray().Count == 2, "No fallback bypasses a selected filtering provider.");
            Check(config["dns"]!["servers"]![0]!["detour"] is null, "Bootstrap must not depend on the proxy it resolves.");
            File.WriteAllText($"artifacts/dns-checks/{provider.Id}.json", config.ToJsonString(Configuration.JsonOptions));
            var restored = PortableConfiguration.Import(PortableConfiguration.Export(settings).ToJsonString(), new Settings());
            Check(ProfileDns.Provider(restored.ActiveProfile!) == provider.Id && restored.ActiveProfile!.Data["dns"]!["future"] is null, "Supported DNS fields round-trip; unknown fields are discarded.");
        }
        Check(Configuration.Signature(settings) != initial, "Changing active DNS requires reconnection.");
        Check(SettingsAnalysis.Prepare(settings, oldAnalysis).Signature != oldAnalysis.Signature, "The analysis cache notices profile DNS changes.");
        var omitted = PortableConfiguration.Export(settings);
        omitted["profiles"]![0]!.AsObject().Remove("dns");
        Check(ProfileDns.Provider(PortableConfiguration.Import(omitted.ToJsonString(), settings).ActiveProfile!) == "YANDEX_FAMILY", "Omitted DNS preserves existing settings.");
        profile.Data["dns"] = new JsonObject { ["provider"] = "CUSTOM", ["customDohUrl"] = "https://[2001:db8::1]:8443/query/test%2Ftoken" };
        var custom = ProfileDns.Build(profile);
        Check(custom["server"]!.ToString() == "2001:db8::1" && custom["server_port"]!.GetValue<int>() == 8443 && custom["path"]!.ToString() == "/query/test%2Ftoken", "Custom URL preserves IPv6, port and escaped path.");
        File.WriteAllText("artifacts/dns-checks/custom.json", Configuration.Build(settings, 9999, "test").ToJsonString(Configuration.JsonOptions));
        foreach (string address in new[] { "https://dns.example/query?token=secret", "http://dns.example/", "tcp://dns.example", "udp://1.1.1.1", "tcp://1.1.1.1:0", "https://user:secret@dns.example/", "https://dns.example/#fragment", "https://dns.example:0/", "https://dns.example/a b" })
        {
            profile.Data["dns"]!["customDohUrl"] = address;
            Reject(() => Configuration.Build(settings, 9999, "test"));
        }
        settings.DefaultAction = "system";
        var system = Configuration.Build(settings, 9999, "test");
        Check(system["dns"]!["servers"]![0]!["type"]!.ToString() == "local" && system["inbounds"]![0]!["dns_mode"]!.ToString() == "disabled", "System mode does not activate profile DNS.");
        profile.Data["dns"] = new JsonObject { ["provider"] = "GOOGLE" };
        settings.Rules.Add(new RoutingRule { Source = "exe", Path = @"C:\Test.exe", Action = "proxy" });
        var beforeBootstrap = SettingsAnalysis.Prepare(settings);
        foreach (var provider in ProfileDns.Providers.Where(item => item.Id != "CUSTOM"))
        {
            settings.BootstrapDnsProvider = provider.Id;
            var config = Configuration.Build(settings, 9999, "test");
            var bootstrap = config["dns"]!["servers"]![0]!;
            Check(System.Net.IPAddress.TryParse(bootstrap["server"]!.ToString(), out _) && bootstrap["detour"] is null && bootstrap["tls"]!["server_name"]!.ToString() == new Uri(provider.Url).Host,
                "Bootstrap connects directly to a known IP and verifies the provider hostname without recursive DNS.");
            Check(config["route"]!["default_domain_resolver"]!.ToString() == "system" && config["outbounds"]![0]!["domain_resolver"]!.ToString() == "bootstrap", "Selective proxy bootstrap does not replace direct application DNS.");
            File.WriteAllText($"artifacts/dns-checks/bootstrap-{provider.Id}.json", config.ToJsonString(Configuration.JsonOptions));
        }
        Check(!SettingsAnalysis.SameNetwork(settings, beforeBootstrap.Settings) && SettingsAnalysis.Prepare(settings, beforeBootstrap).Signature != beforeBootstrap.Signature, "Bootstrap changes invalidate cached network analysis.");
        var roundTrip = PortableConfiguration.Import(PortableConfiguration.Export(settings).ToJsonString(), new Settings());
        Check(roundTrip.BootstrapDnsProvider == "YANDEX_FAMILY", "Bootstrap choice round-trips in Windows options.");
        var missingBootstrap = PortableConfiguration.Export(settings);
        missingBootstrap["windows"]!.AsObject().Remove("bootstrapDnsProvider");
        Check(PortableConfiguration.Import(missingBootstrap.ToJsonString(), settings).BootstrapDnsProvider == "YANDEX_FAMILY", "An omitted bootstrap setting preserves the current choice.");
        Check(ProfileDns.BuildBootstrap("system", true)["type"]!.ToString() == "local", "Explicit Windows DNS works in proxy-default mode.");
        Reject(() => ProfileDns.BuildBootstrap("CUSTOM", false));
        profile.Data["dns"] = new JsonObject { ["provider"] = "FUTURE_PROVIDER" };
        Reject(() => ProfileDns.Build(profile));
        Check(ProfileDns.Provider(PortableConfiguration.Import(PortableConfiguration.Export(settings).ToJsonString(), new Settings()).ActiveProfile!) == "FUTURE_PROVIDER", "Unknown providers survive import instead of silently falling back.");
        profile.Data["dns"] = new JsonObject { ["provider"] = 3 };
        Reject(() => PortableConfiguration.ValidateProfile(profile));
        profile.Data["dns"] = "invalid";
        Reject(() => PortableConfiguration.ValidateProfile(profile));
        Console.WriteLine("Profile DNS providers, routing, compatibility and validation passed.");
    }
}
