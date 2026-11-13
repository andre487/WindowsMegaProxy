using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

static class ConnectionOptionsChecks
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var profile = ProxyProfile.Create("Jump");
        profile.Proxy["type"] = "SSH_JUMP"; profile.Proxy["host"] = "destination.example"; profile.Proxy["port"] = 22;
        profile.Proxy["username"] = "exit"; profile.Proxy["password"] = "exit-secret";
        profile.Proxy["jump"] = new JsonObject { ["host"] = "jump.example", ["port"] = 2222, ["sameAuthentication"] = false, ["username"] = "first", ["password"] = "first-secret" };
        string key = "ssh-ed25519 " + Convert.ToBase64String(Convert.FromHexString("0000000B7373682D6564323535313900000020" + new string('0', 64)));
        string fingerprint = SshConfiguration.Fingerprint(key);
        Check(SshConfiguration.ApplyKey(profile, new(profile.Id, profile.Name, "jump.example", 2222, "", fingerprint, key, "jump")), "Pin jump key separately.");
        Check(SshConfiguration.ApplyKey(profile, new(profile.Id, profile.Name, profile.Host, 22, "", fingerprint, key)), "Pin destination key separately.");
        Check(!SshConfiguration.ApplyKey(profile, new(profile.Id, profile.Name, "other.example", 22, fingerprint, fingerprint, key)), "Do not save a key after endpoint changes.");
        var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy", TunMtu = 1400 };
        var config = Configuration.Build(settings, 9999, "test");
        var exit = config["outbounds"]![0]!; var jump = config["outbounds"]![1]!;
        Check(exit["type"]!.ToString() == "ssh" && exit["detour"]!.ToString() == "jump" && exit["domain_resolver"] is null, "Destination SSH must resolve through the first hop.");
        Check(jump["password"]!.ToString() == "first-secret" && exit["password"]!.ToString() == "exit-secret" && jump["domain_resolver"]!.ToString() == "bootstrap", "Separate jump authentication and bootstrap.");
        Check(jump["host_key"] is null && exit["host_key"] is null && jump["host_key_algorithms"] is null && exit["host_key_algorithms"] is null, "Both SSH hops accept any server key, including with imported pins.");
        string originalSignature = Configuration.Signature(settings);
        profile.Proxy["jump"]!["acceptAnyHostKey"] = true;
        Check(Configuration.Signature(settings) != originalSignature, "Changing jump trust requires reconnection.");
        profile.Proxy["jump"]!.AsObject().Remove("acceptAnyHostKey");
        settings.SharedOptions["ssh"] = new JsonObject { ["authMode"] = "PASSWORD_ONLY" };
        profile.Proxy["privateKey"] = "not used in password-only mode";
        Check(Configuration.Build(settings, 9999, "test")["outbounds"]![0]!["private_key"] is null, "Password-only mode must not parse or send a stored private key.");
        profile.Proxy.Remove("privateKey");
        Check(SshConfiguration.AuthMode(PortableConfiguration.Import(PortableConfiguration.Export(settings).ToJsonString(), new Settings())) == "PASSWORD_ONLY", "SSH authentication mode round-trips through the shared configuration.");
        File.WriteAllText("artifacts/ssh-jump.json", config.ToJsonString(Configuration.JsonOptions));
        Check(config["inbounds"]![0]!["mtu"]!.GetValue<int>() == 1400, "Apply local MTU.");
        var before = SettingsAnalysis.Prepare(settings);
        profile.Data["routing"] = new JsonObject { ["allowIpv6"] = false };
        Check(Configuration.Signature(settings) == before.Signature, "Explicit false matches the default IPv6 permission.");
        config = Configuration.Build(settings, 9999, "test");
        Check(config["route"]!["rules"]!.AsArray().Any(rule => rule?["ip_version"]?.ToString() == "6"), "Windows enforces imported IPv6 restrictions.");
        Check(config["dns"]!["strategy"]!.ToString() == "ipv4_only", "IPv6 is disabled by default.");
        profile.Data["dns"] = new JsonObject { ["provider"] = "CUSTOM", ["customDohUrl"] = "tcp://1.1.1.1:5353" };
        Check(ProfileDns.Build(profile)["detour"]!.ToString() == "proxy", "Plain TCP DNS never bypasses the proxy.");
        File.WriteAllText("artifacts/dns-checks/plain-tcp.json", Configuration.Build(settings, 9999, "test").ToJsonString(Configuration.JsonOptions));
        profile.Proxy["type"] = "SOCKS5";
        profile.Data["dns"]!["customDohUrl"] = "udp://1.1.1.1";
        Check(ProfileDns.Build(profile)["server_port"]!.GetValue<int>() == 53, "Default plain DNS port.");
        File.WriteAllText("artifacts/dns-checks/plain-udp.json", Configuration.Build(settings, 9999, "test").ToJsonString(Configuration.JsonOptions));
        Check(ConnectionSnapshot.Bytes(1000, "SI") == "1 KB" && ConnectionSnapshot.Bytes(1024) == "1 KiB", "Traffic units preserve decimal and binary boundaries.");
    }
}
