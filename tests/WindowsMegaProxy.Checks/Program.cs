using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text.Json.Nodes;
using WindowsMegaProxy;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception error) when (error is ArgumentException or InvalidDataException) { return; }
    throw new Exception("Invalid settings were accepted.");
}

Assert(ProfileAppearance.Colors.Count == 12 && ProfileAppearance.Countries.Count >= 250, "Bundle the palette and country catalog.");
Assert(ProfileAppearance.CountryName("DE", "fallback", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) == "Германия", "Country names follow the app language.");
Assert(ProfileAppearance.CountryName("DE", "fallback", System.Globalization.CultureInfo.GetCultureInfo("en-US")) == "Germany", "Country localization uses the requested language.");
Assert(ProfileAppearance.Country("DE").FlagUri!.EndsWith("/DE.png"), "Country codes resolve to embedded flags.");
Assert(ProfileAppearance.Country("ZZ").FlagUri is null, "Unknown countries must not load missing resources.");
Assert(ProfileAppearance.ColorForCountry("ZZ", 5) == 5, "Unknown flags preserve the fallback color.");
Assert(ProfileAppearance.ColorForCountry("JP", 5) == 0, "Dominant red in the Japanese flag maps to the Fluent red slot.");
var colored = Enumerable.Range(0, ProfileAppearance.Colors.Count).Select(index =>
{
    var item = ProxyProfile.Create(); item.Data["color"] = index; return item;
}).ToList();
Assert(ProfileAppearance.NextColor(colored.Take(4)) == 4, "Unused colors are assigned first.");
colored.Add(colored[0].Copy());
Assert(ProfileAppearance.NextColor(colored) == 1, "Once the palette is used, choose a least-used color with a stable tie-break.");
var explicitColor = ProxyProfile.Create(); explicitColor.Data["color"] = 123;
var inferredColor = ProxyProfile.Create(); inferredColor.Data["countryCode"] = "JP";
ProfileAppearance.AssignMissingColors([explicitColor, inferredColor]);
Assert(explicitColor.Data["color"]!.GetValue<int>() == 123 && inferredColor.ColorIndex == 0, "Keep explicit imported colors and derive missing colors from flags.");

if (args.Length == 2 && args[0] == "--real-config")
{
    var realText = PortableConfiguration.ReadFile(args[1]);
    var realSettings = PortableConfiguration.Import(realText, new Settings());
    var inMemoryExport = PortableConfiguration.Export(realSettings, true, true);
    var source = JsonNode.Parse(realText)!.AsObject();
    Assert(JsonNode.DeepEquals(source["profiles"], inMemoryExport["profiles"]), "Real profiles must survive a lossless round trip.");
    Assert(JsonNode.DeepEquals(source["subscription"], inMemoryExport["subscription"]), "Real subscription must survive a round trip.");
    Console.WriteLine($"Read-only real configuration round trip passed ({realSettings.Profiles.Count} profiles). No configuration files written.");
    return;
}

var pattern = Configuration.FolderPattern(@"C:\Apps\A+B");
Assert(Regex.IsMatch(@"c:\apps\a+b\nested\client.exe", pattern), "Recursive case-insensitive folder match.");
Assert(!Regex.IsMatch(@"C:\Apps\A+B-Other\client.exe", pattern), "Sibling folder must not match.");
Assert(!Regex.IsMatch(@"C:\Apps\AB\client.exe", pattern), "Regex characters in paths must be literal.");
Assert(Regex.IsMatch(@"C:\anything.exe", Configuration.FolderPattern(@"C:\")), "Drive root folder.");
Reject(() => Configuration.FolderPattern("relative"));

var exe = Configuration.BuildRule(new RoutingRule { Source = "exe", Path = @"C:\Apps\a+b.exe" });
var exePattern = exe["process_path_regex"]![0]!.ToString();
Assert(Regex.IsMatch(@"c:\apps\A+B.EXE", exePattern), "EXE casing.");
Assert(!Regex.IsMatch(@"C:\Apps\a+b.exe.old", exePattern), "EXE must match exactly.");
var rule = Configuration.BuildRule(new RoutingRule { Source = "folder", Path = @"C:\Apps", Destination = "*.example.com", Action = "direct" });
Assert(rule["domain_suffix"]![0]!.ToString() == "example.com" && rule["outbound"]!.ToString() == "direct", "Combined source and domain rule.");
Assert(Configuration.BuildRule(new RoutingRule { Destination = "2001:db8::1" })["ip_cidr"]![0]!.ToString().EndsWith("/128"), "IPv6 host prefix.");
Assert(Configuration.BuildRule(new RoutingRule { Action = "block" })["outbound"] is null, "Reject must not select outbound.");
Assert(new RoutingRule { ProfileId = "id", ProfileName = "Named profile" }.ActionLabel == Ui.T("Active profile"), "Rules always use the active profile.");
Reject(() => Configuration.BuildRule(new RoutingRule { Action = "unknown" }));
Reject(() => Configuration.BuildRule(new RoutingRule { Source = "exe", Path = "a.exe" }));
Reject(() => Configuration.BuildRule(new RoutingRule { Destination = "192.0.2.1/99" }));
Reject(() => Configuration.BuildRule(new RoutingRule { Destination = "https://example.com" }));

var profile = ProxyProfile.Create("Test proxy");
profile.Proxy["host"] = "proxy.example.com";
var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, DefaultAction = "proxy" };
Assert(profile.SelectionLabel == "Test proxy · HTTPS", "Profile picker includes the transport type.");
settings.Rules.Add(new RoutingRule { Destination = "example.com", Action = "block" });
var config = Configuration.Build(settings, 9999, "test-secret");
Assert(config["outbounds"]![0]!["tls"]!["enabled"]!.GetValue<bool>(), "HTTPS must verify TLS.");
Assert(!config["outbounds"]![0]!["tls"]!["insecure"]!.GetValue<bool>(), "No insecure default.");
Assert(config["route"]!["default_domain_resolver"]!.ToString() == "bootstrap", "Proxy DNS must not recursively use itself.");
Assert(config["dns"]!["final"]!.ToString() == "dns", "Application DNS uses proxy DoH.");
Assert(config["inbounds"]![0]!["strict_route"]!.GetValue<bool>(), "Prevent ordinary DNS leakage.");
profile.Proxy["password"] = "password";
Assert(Configuration.Build(settings, 9999, "test-secret")["outbounds"]![0]!["password"]!.ToString() == "password", "Password with empty username must not be discarded.");
settings.DefaultAction = "block";
var blocked = Configuration.Build(settings, 9999, "test-secret");
Assert(blocked["route"]!["rules"]!.AsArray()[^1]!["action"]!.ToString() == "reject", "Block default must terminate unmatched traffic.");
settings.DefaultAction = "proxy";
profile.Proxy["port"] = 0; Reject(() => Configuration.Build(settings, 9999, "test-secret")); profile.Proxy["port"] = 443;
profile.Proxy["host"] = "https://proxy.example.com"; Reject(() => Configuration.Build(settings, 9999, "test-secret"));
profile.Proxy["host"] = "proxy.example.com";

var portable = JsonNode.Parse("""
{
  "schema": "net.megaproxy487.config", "version": 8, "activeProfileId": "team",
  "routing": { "selectedPackages": ["android.only"], "routeAllApps": false },
  "browser": { "theme": "dark", "routing": { "domains": ["*.example.net"] } },
  "future": { "nested": [1, true, "preserve"] },
  "subscription": { "url": "https://config.example/team.json", "username": "team", "password": "subscription-secret" },
  "profiles": [
    { "id": "team", "name": "Team", "color": 3, "countryCode": "US",
      "proxy": { "type": "HTTPS", "host": "proxy.example.com", "port": 443, "username": "user", "password": "profile-secret" },
      "browser": { "bypass": ["intranet.example"] }, "dns": { "provider": "QUAD9" }, "futureProfile": { "retain": true } },
    { "id": "ssh", "proxy": { "type": "SSH", "host": "ssh.example.com", "port": 22, "privateKey": "private-key" } }
  ],
  "windows": { "version": 1, "defaultAction": "direct", "bypassLocalNetworks": false, "autoConnect": true,
    "launchAtSignIn": true, "futureWindows": ["retain"],
    "rules": [{ "source": "folder", "path": "C:\\Apps", "destination": "*.example.com", "action": "proxy", "profileId": "team" }] }
}
""")!.AsObject();
var imported = PortableConfiguration.Import(portable.ToJsonString(), new Settings());
Assert(imported.Profiles.Count == 2 && imported.ActiveProfileId == "team", "Import profiles and selected ID.");
Assert(imported.DefaultAction == "system" && !imported.BypassLan && imported.AutoConnect && imported.LaunchAtSignIn == true, "Apply Windows options only.");
Assert(imported.Profiles[1].Supported, "Imported SSH profiles are supported without changing their fields.");
var exported = PortableConfiguration.Export(imported, true, true);
foreach (var key in new[] { "routing", "browser", "future", "subscription" })
    Assert(JsonNode.DeepEquals(portable[key], exported[key]), "Preserve other platforms and unknown root fields: " + key);
Assert(JsonNode.DeepEquals(portable["profiles"], exported["profiles"]), "Preserve all profile fields including unsupported transports.");
Assert(exported["windows"]!["futureWindows"]![0]!.ToString() == "retain", "Preserve unknown Windows fields.");
var hidden = PortableConfiguration.Export(imported);
Assert(hidden["profiles"]![0]!["proxy"]!["password"] is null && hidden["profiles"]![1]!["proxy"]!["privateKey"] is null && hidden["subscription"]!["password"] is null, "Redact secrets recursively by default.");
var repeated = PortableConfiguration.Import(hidden.ToJsonString(), imported);
Assert(repeated.Profiles.Count == 2 && repeated.Profiles[0].Proxy["password"]!.ToString() == "profile-secret", "Reimport updates IDs and keeps omitted credentials for unchanged endpoints.");
Assert(repeated.SharedOptions["subscription"]!["password"]!.ToString() == "subscription-secret", "Retain same subscription account password.");
var replacedEndpoint = (JsonObject)hidden.DeepClone();
replacedEndpoint["profiles"]![0]!["proxy"]!["host"] = "other.example.com";
replacedEndpoint["subscription"]!["url"] = "https://replacement.example/team.json";
var replaced = PortableConfiguration.Import(replacedEndpoint.ToJsonString(), imported);
Assert(replaced.Profiles[0].Proxy["password"] is null && replaced.SharedOptions["subscription"]!["password"] is null, "Never reuse credentials for changed endpoints.");
var clearedPassword = (JsonObject)hidden.DeepClone();
clearedPassword["profiles"]![0]!["proxy"]!["password"] = "";
Assert(PortableConfiguration.Import(clearedPassword.ToJsonString(), imported).Profiles[0].Proxy["password"]!.ToString() == "", "Explicit empty password clears it.");
var foreignOnly = (JsonObject)portable.DeepClone();
foreignOnly.Remove("windows");
var noWindows = PortableConfiguration.Import(foreignOnly.ToJsonString(), new Settings());
Assert(noWindows.DefaultAction == "system" && noWindows.BypassLan && !noWindows.AutoConnect, "Android/browser routing must not alter Windows routing.");
var local = ProxyProfile.Create("Local"); local.Proxy["host"] = "local.example.com";
imported.Profiles.Add(local);
Assert(PortableConfiguration.Import(hidden.ToJsonString(), imported).Profiles.Count == 3, "Import must not delete local profiles missing from the file.");
var duplicate = (JsonObject)portable.DeepClone();
duplicate["profiles"]!.AsArray().Add(duplicate["profiles"]![0]!.DeepClone());
Reject(() => PortableConfiguration.Import(duplicate.ToJsonString(), imported));
Reject(() => PortableConfiguration.Import("{\"schema\":1,\"schema\":2}", imported));
var invalidRules = (JsonObject)portable.DeepClone();
invalidRules["windows"]!["rules"]![0]!["profileId"] = "missing";
Assert(PortableConfiguration.Import(invalidRules.ToJsonString(), imported).Rules[0].ProfileId is null, "Legacy named-profile rules migrate to the active profile.");
var unknownRule = (JsonObject)portable.DeepClone();
unknownRule["windows"]!["rules"]![0]!["unknownConstraint"] = true;
Reject(() => PortableConfiguration.Import(unknownRule.ToJsonString(), imported));
var changedAccount = (JsonObject)hidden.DeepClone();
changedAccount["profiles"]![0]!["proxy"]!["username"] = "other-account";
Assert(PortableConfiguration.Import(changedAccount.ToJsonString(), imported).Profiles[0].Proxy["password"] is null, "Never reuse a password for a changed account.");
var untrusted = (JsonObject)portable.DeepClone();
untrusted["profiles"]![0]!["proxy"]!["allowInvalidProxyCertificate"] = true;
var trustedState = PortableConfiguration.Import(untrusted.ToJsonString(), new Settings());
Assert(PortableConfiguration.Import(replacedEndpoint.ToJsonString(), trustedState).Profiles[0].Proxy["allowInvalidProxyCertificate"] is null, "Certificate exceptions do not migrate to a replacement endpoint.");
var jumpDocument = JsonNode.Parse("""
{ "schema": "net.megaproxy487.config", "version": 8, "profiles": [
  { "id": "jump", "proxy": { "type": "HTTPS_JUMP", "host": "exit.example.com", "port": 443,
    "username": "user", "password": "exit-secret", "jump": { "host": "jump.example.com", "port": 443, "sameAuthentication": true } } }
] }
""")!.AsObject();
var jumpState = PortableConfiguration.Import(jumpDocument.ToJsonString(), new Settings());
var newJump = PortableConfiguration.Export(jumpState);
newJump["profiles"]![0]!["proxy"]!["jump"]!["host"] = "replacement-jump.example.com";
Assert(PortableConfiguration.Import(newJump.ToJsonString(), jumpState).Profiles[0].Proxy["password"] is null, "Shared Jump credentials require both endpoints to remain unchanged.");
Reject(() => PortableConfiguration.Import(new string(' ', PortableConfiguration.MaximumBytes + 1), imported));
var example = PortableConfiguration.Import(File.ReadAllText("examples/MegaProxy.json"), new Settings());
Assert(example.Profiles.Count == 2 && example.Rules[0].ProfileId is null, "The checked-in example must import.");
var snapshot = PortableConfiguration.Export(imported, true, true).ToJsonString();
var version = (JsonObject)portable.DeepClone(); version["version"] = 99;
Reject(() => PortableConfiguration.Import(version.ToJsonString(), imported));
Assert(PortableConfiguration.Export(imported, true, true).ToJsonString() == snapshot, "Failed import leaves current settings unchanged.");
var futureWindows = (JsonObject)portable.DeepClone(); futureWindows["windows"]!["version"] = 2;
var futureSettings = PortableConfiguration.Import(futureWindows.ToJsonString(), new Settings());
Assert(!futureSettings.AutoConnect && JsonNode.DeepEquals(PortableConfiguration.Export(futureSettings, true, true)["windows"], futureWindows["windows"]), "Future Windows options pass through without execution or downgrade.");
var routed = Configuration.Build(imported, 9999, "test-secret");
Assert(routed["outbounds"]!.AsArray().Any(item => item!["tag"]!.ToString() == "proxy"), "Compile a rule's selected profile.");
imported.ActiveProfileId = "ssh";
imported.DefaultAction = "proxy";
Reject(() => Configuration.Build(imported, 9999, "test-secret"));

if (args.Length >= 1)
{
    settings.DefaultAction = "proxy";
    settings.Rules.Add(new RoutingRule { Source = "folder", Path = @"C:\Apps\A+B", Destination = "*.example.net", Action = "direct" });
    File.WriteAllText(args[0], Configuration.Build(settings, 9999, "test-secret").ToJsonString(Configuration.JsonOptions));
    imported.ActiveProfileId = "team";
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[0])!, "synthetic-portable-test.json"), PortableConfiguration.Export(imported).ToJsonString(Configuration.JsonOptions));
}
Console.WriteLine("All configuration checks passed.");
await DiagnosticsChecks.RunAsync();
await TraceProbeChecks.RunAsync();
await AuthenticationChecks.RunAsync();

var connectionsJson = JsonNode.Parse("""
{"uploadTotal":4096,"downloadTotal":8192,"connections":[
 {"metadata":{"processPath":"C:\\Apps\\default.exe","host":"example.com","destinationPort":"443","network":"tcp"},"chains":["direct"],"rule":"final","upload":100,"download":200},
 {"metadata":{"processPath":"C:\\Apps\\exception.exe","destinationIP":"2001:db8::1","destinationPort":"443","network":"tcp"},"chains":["direct"],"rule":"process_path_regex => route(direct)","upload":1024,"download":2048}
]}
""")!;
var hiddenDefaults = ConnectionSnapshot.Parse(connectionsJson, new Settings(), false);
Assert(hiddenDefaults.Rows.Count == 1 && hiddenDefaults.Rows[0].Process.EndsWith("exception.exe"), "Hide fallback connections, retaining explicit rules using the same outbound.");
Assert(hiddenDefaults.Rows[0].Route == Ui.T("System") && hiddenDefaults.Rows[0].Destination == "[2001:db8::1]:443", "Human readable routes and IPv6.");
Assert(hiddenDefaults.Upload == 4096 && hiddenDefaults.Download == 8192 && hiddenDefaults.Total == 2, "Traffic includes short-lived and hidden connections.");
Assert(ConnectionSnapshot.Parse(connectionsJson, new Settings(), true).Rows.Count == 2, "Show all connections on request.");
Assert(ConnectionSnapshot.RouteName("profile:" + profile.Id, settings) == profile.Name, "Profile tags resolve to names.");
var sizeSettings = PortableConfiguration.Copy(settings); sizeSettings.LogSizeMb = 25;
Assert(PortableConfiguration.Import(PortableConfiguration.Export(sizeSettings).ToJsonString(), new Settings()).LogSizeMb == 25, "Log size round trip in Windows options.");
sizeSettings.LogSizeMb = 101; Reject(() => PortableConfiguration.ValidateSettings(sizeSettings));
string logDirectory = Path.GetFullPath("artifacts/log-checks-" + Guid.NewGuid().ToString("N"));
var diagnostic = new DiagnosticLog(logDirectory);
diagnostic.SetLimit(5);
for (int i = 0; i < 600; i++) diagnostic.Write(new string('x', 4000));
diagnostic.SetLimit(1);
for (int i = 0; i < 400; i++) diagnostic.Write(new string('y', 4000));
diagnostic.Write("last-entry");
Assert(Directory.GetFiles(logDirectory).Length <= 2 && Directory.GetFiles(logDirectory).Sum(file => new FileInfo(file).Length) <= 1024 * 1024, "Rotation and reducing the size keep the total disk budget bounded.");
Assert(diagnostic.Read(true).EndsWith("last-entry" + Environment.NewLine) && diagnostic.Read(true).Length <= 64000, "UI reads only a bounded log tail.");
string redacted = DiagnosticLog.Redact("password=topsecret {\"token\":\"two words\"} https://user:password@example.com/?secret=abc");
Assert(!redacted.Contains("topsecret") && !redacted.Contains("two words") && !redacted.Contains("user:") && !redacted.Contains("abc"), "Redact credentials and URL tokens.");

var systemConfig = Configuration.Build(new Settings(), 9999, "test");
Assert(systemConfig["route"]!["final"]!.ToString() == "direct", "System mode needs no profile.");
Assert(systemConfig["inbounds"]![0]!["dns_mode"]!.ToString() == "disabled" && !systemConfig["inbounds"]![0]!["strict_route"]!.GetValue<bool>(), "System mode preserves Windows DNS.");
Assert(systemConfig["dns"]!["servers"]![0]!["type"]!.ToString() == "local", "System DNS resolver.");
Assert(!systemConfig.ToJsonString().Contains("hijack-dns"), "No unconditional DNS interception in system mode.");
var exceptions = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, Rules = [new RoutingRule { Destination = "example.com" }] };
Assert(Configuration.Build(exceptions, 9999, "test")["outbounds"]!.AsArray().Count == 2, "A system-mode exception uses the active profile.");
File.WriteAllText("artifacts/system-exceptions-test-config.json", Configuration.Build(exceptions, 9999, "test").ToJsonString(Configuration.JsonOptions));
Reject(() => Configuration.Build(new Settings { Rules = [new RoutingRule()] }, 9999, "test"));
File.WriteAllText("artifacts/system-test-config.json", systemConfig.ToJsonString(Configuration.JsonOptions));

Assert(Ui.DefaultLanguage(System.Globalization.CultureInfo.GetCultureInfo("de-DE")) == "en", "English is the fallback language.");
Assert(Ui.DefaultLanguage(System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) == "ru", "Russian Windows selects Russian initially.");
Assert(Ui.Russian.Keys.All(key => !Regex.IsMatch(key, "[А-Яа-яЁё]")), "Resource keys are English.");
Ui.SetLanguage("en");
Assert(Ui.T("Settings") == "Settings" && ProfileAppearance.Country("DE").Name == "Germany", "English source strings and countries.");
Ui.SetLanguage("ru");
Assert(Ui.T("Settings") == "Настройки" && ProfileAppearance.Country("DE").Name == "Германия", "Russian translations and countries refresh.");
Ui.SetLanguage("en");
var preferences = new Settings { Profiles = [profile], ActiveProfileId = profile.Id, Language = "en", Theme = "dark", ShowAllConnections = true };
var restoredPreferences = PortableConfiguration.Import(PortableConfiguration.Export(preferences).ToJsonString(), new Settings());
Assert(restoredPreferences.Language == "en" && restoredPreferences.Theme == "dark" && restoredPreferences.ShowAllConnections, "Preferences survive a portable round trip.");
foreach (string fingerprint in Configuration.TlsFingerprints)
{
    preferences.TlsFingerprint = fingerprint;
    preferences.DefaultAction = "proxy";
    var output = Configuration.Build(preferences, 9999, "test");
    Assert(output["outbounds"]![0]!["tls"]!["utls"]?["fingerprint"]?.ToString() == (fingerprint == "default" ? null : fingerprint), "Apply the selected TCP fingerprint.");
    Assert(!Configuration.RequiresHttp3(preferences), "A browser fingerprint keeps the stable core.");
    Assert(PortableConfiguration.Import(PortableConfiguration.Export(preferences).ToJsonString(), new Settings()).TlsFingerprint == fingerprint, "Fingerprint round trip.");
    File.WriteAllText($"artifacts/fingerprint-{fingerprint}.json", output.ToJsonString(Configuration.JsonOptions));
}
var h3Profile = profile.Copy(); h3Profile.Proxy["type"] = "MASQUE";
var h3Settings = new Settings { Profiles = [h3Profile], ActiveProfileId = h3Profile.Id, DefaultAction = "proxy", TlsFingerprint = "chrome" };
Assert(h3Profile.Supported && Configuration.RequiresHttp3(h3Settings), "Explicit MASQUE selects the HTTP/3 core.");
var h3Config = Configuration.Build(h3Settings, 9999, "test");
Assert(h3Config["outbounds"]![0]!["version"]!.GetValue<int>() == 3 && h3Config["outbounds"]![0]!["disable_version_fallback"]!.GetValue<bool>(), "MASQUE requires HTTP/3 without implicit TCP downgrade.");
Assert(h3Config["outbounds"]![0]!["tls"]!["utls"] is null, "HTTP/3 never receives unsupported TCP uTLS options.");
File.WriteAllText("artifacts/http3-test-config.json", h3Config.ToJsonString(Configuration.JsonOptions));
h3Profile.Proxy["type"] = "HTTPS"; h3Profile.Proxy["preferHttp3"] = true;
Assert(Configuration.RequiresHttp3(h3Settings) && h3Profile.PreferHttp3, "HTTPS preference selects the HTTP/3 transport.");
var h3Restored = PortableConfiguration.Import(PortableConfiguration.Export(h3Settings, true).ToJsonString(), new Settings());
Assert(h3Restored.ActiveProfile!.PreferHttp3 && h3Restored.TlsFingerprint == "chrome", "Preserve the shared preferHttp3 flag and Windows fingerprint.");
h3Settings.DefaultAction = "system";
Assert(!Configuration.RequiresHttp3(h3Settings), "An unused HTTP/3 profile does not select alpha.");
h3Profile.Proxy["preferHttp3"] = "true"; Reject(() => PortableConfiguration.ValidateProfile(h3Profile));
Assert(Http3Fallback.IsUnavailable(["HTTP/3 unavailable: timeout: no recent network activity"]), "Known QUIC timeouts permit stable fallback.");
Assert(!Http3Fallback.IsUnavailable(["HTTP/3 unavailable: x509: certificate expired; no recent network activity"]), "Certificate errors never downgrade.");
Assert(!Http3Fallback.IsUnavailable(["HTTP/3 unavailable: authentication required (407)"]), "Authentication errors never downgrade.");
Assert(!Http3Fallback.IsUnavailable(["HTTP/3 CONNECT: unexpected status: 403"]), "Access denials never downgrade.");
Assert(!Http3Fallback.IsUnavailable(["unknown failure"]), "Unknown failures never downgrade.");
var grouped = ConnectionSnapshot.Parse(JsonNode.Parse("""
{"connections":[
 {"chains":["direct"],"rule":"final"},
 {"chains":["proxy"],"rule":"domain => route(proxy)","upload":1024},
 {"chains":[],"rule":"domain => reject"},
 {"chains":["direct"],"rule":"domain reject.example => route(direct)"}
]}
""")!, settings, true);
Assert(grouped.Rows.Select(row => row.RouteGroup).SequenceEqual(new[] { 2, 0, 1, 2 }), "Classify proxy, blocked and system connections by route metadata, not translated labels.");
Assert(grouped.Rows[1].UploadedBytes == 1024, "Keep numeric transfer values for correct sorting.");
string sshHostKey = "ssh-ed25519 " + Convert.ToBase64String(Convert.FromHexString("0000000B7373682D6564323535313900000020" + new string('0', 64)));
var sshProfile = ProxyProfile.Create("SSH"); sshProfile.Proxy["type"] = "SSH"; sshProfile.Proxy["host"] = "ssh.example.com"; sshProfile.Proxy["port"] = 22;
sshProfile.Proxy["username"] = "user"; sshProfile.Proxy["password"] = "secret";
sshProfile.Proxy["trustedHostKey"] = SshConfiguration.Fingerprint(sshHostKey);
sshProfile.Data["windows"] = new JsonObject { ["sshHostKey"] = sshHostKey };
var sshSettings = new Settings { Profiles = [sshProfile], ActiveProfileId = sshProfile.Id, DefaultAction = "proxy" };
Assert(!Configuration.RequiresHttp3(sshSettings), "SSH always uses the stable core.");
foreach (string fingerprint in SshConfiguration.Profiles)
{
    sshProfile.Proxy["sshProfile"] = fingerprint;
    var configSsh = Configuration.Build(sshSettings, 9999, "test");
    Assert(configSsh["outbounds"]![0]!["type"]!.ToString() == "ssh" && configSsh["outbounds"]![0]!["host_key"]![0]!.ToString() == sshHostKey && configSsh["outbounds"]![0]!["tls"] is null, "SSH uses native key pinning rather than TLS or an unverified host.");
    File.WriteAllText($"artifacts/ssh-{fingerprint}.json", configSsh.ToJsonString(Configuration.JsonOptions));
}
var restoredSsh = PortableConfiguration.Import(PortableConfiguration.Export(sshSettings, true, true).ToJsonString(), new Settings());
Assert(SshConfiguration.PublicKey(restoredSsh.ActiveProfile!) == sshHostKey && restoredSsh.ActiveProfile!.Proxy["trustedHostKey"]!.ToString() == sshProfile.Proxy["trustedHostKey"]!.ToString(), "SSH raw key and Android-compatible SHA-256 fingerprint survive portable round trips.");
sshProfile.Proxy["trustedHostKey"] = "SHA256:wrong"; Reject(() => Configuration.Build(sshSettings, 9999, "test"));
sshProfile.Data["windows"] = new JsonObject(); Reject(() => Configuration.Build(sshSettings, 9999, "test"));
Reject(() => SshConfiguration.Fingerprint("ssh-ed25519 invalid-base64"));
var jumpProfile = profile.Copy(); jumpProfile.Proxy["type"] = "HTTPS_JUMP";
jumpProfile.Proxy["username"] = "exit"; jumpProfile.Proxy["password"] = "exit-secret";
jumpProfile.Proxy["jump"] = new JsonObject { ["host"] = "jump.example.com", ["port"] = 443, ["sameAuthentication"] = false, ["username"] = "jump", ["password"] = "jump-secret" };
var chainSettings = new Settings { Profiles = [jumpProfile], ActiveProfileId = jumpProfile.Id, DefaultAction = "proxy", TlsFingerprint = "chrome" };
var chainConfig = Configuration.Build(chainSettings, 9999, "test");
Assert(chainConfig["outbounds"]![0]!["detour"]!.ToString() == "jump" && chainConfig["outbounds"]![1]!["tag"]!.ToString() == "jump", "The exit proxy requires the Jump outbound.");
Assert(chainConfig["outbounds"]![0]!["password"]!.ToString() == "exit-secret" && chainConfig["outbounds"]![1]!["password"]!.ToString() == "jump-secret", "Keep separate credentials for both hops.");
Assert(chainConfig["outbounds"]![0]!["domain_resolver"] is null, "Exit-host DNS is left to the Jump detour.");
Assert(chainConfig["outbounds"]!.AsArray().Take(2).All(item => item!["tls"]!["alpn"]![0]!.ToString() == "http/1.1"), "Stable CONNECT never negotiates unsupported HTTP/2 on either hop.");
File.WriteAllText("artifacts/jump-test-config.json", chainConfig.ToJsonString(Configuration.JsonOptions));
jumpProfile.Proxy["jump"]!["sameAuthentication"] = true;
Assert(Configuration.Build(chainSettings, 9999, "test")["outbounds"]![1]!["password"]!.ToString() == "exit-secret", "Shared Jump credentials use the main profile.");
jumpProfile.Proxy["preferHttp3"] = true;
var h3Chain = Configuration.Build(chainSettings, 9999, "test");
Assert(Configuration.RequiresHttp3(chainSettings) && h3Chain["outbounds"]!.AsArray().Take(2).All(item => item!["version"]!.GetValue<int>() == 3), "H3 preference applies to both mandatory Jump hops.");
File.WriteAllText("artifacts/jump-http3-test-config.json", h3Chain.ToJsonString(Configuration.JsonOptions));
