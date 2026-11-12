using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsMegaProxy;

internal static class Ipv6UiChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run(Window owner)
    {
        foreach (string type in new[] { "HTTPS", "HTTPS_JUMP", "SOCKS5", "SSH", "SSH_JUMP", "MASQUE" })
        {
            var profile = ProxyProfile.Create("IPv6 " + type);
            profile.Proxy["type"] = type; profile.Proxy["host"] = "2001:db8::1";
            profile.Proxy["username"] = "test"; profile.Proxy["password"] = "secret";
            if (type.EndsWith("_JUMP", StringComparison.Ordinal)) profile.Proxy["jump"] = new JsonObject
            { ["host"] = "2001:db8::2", ["port"] = 443, ["sameAuthentication"] = true };
            foreach (bool enabled in new[] { false, true })
            {
                profile.Data["routing"] = new JsonObject { ["allowIpv6"] = enabled };
                var editor = new ProfileWindow(profile)
                { Owner = owner, Left = -10000, Top = -10000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                editor.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Assert(editor.FindName("AllowIpv6Box") is null, "Windows does not offer an ineffective IPv6 switch.");
                    Assert(((TextBox)editor.FindName("HostBox")).Text == "2001:db8::1", type + ": IPv6 host survives opening the editor.");
                    typeof(ProfileWindow).GetMethod("SaveClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, [editor, new RoutedEventArgs()]);
                    if (editor.DialogResult != true) { editor.Close(); throw new Exception(type + ": IPv6 profile could not be saved."); }
                }));
                Assert(editor.ShowDialog() == true, type + ": IPv6 profile saved.");
                profile = editor.Profile!;
                Assert(profile.AllowIpv6 && profile.Host == "2001:db8::1", type + ": IPv6 preference round-trips through the editor.");
                var settings = new Settings { Profiles = [profile], ActiveProfileId = profile.Id };
                var restored = PortableConfiguration.Import(PortableConfiguration.Export(settings, includePasswords: true).ToJsonString(), new Settings()).ActiveProfile!;
                Assert(restored.AllowIpv6 && restored.Host == profile.Host, type + ": Export/import preserves IPv6 settings.");
                if (type.EndsWith("_JUMP", StringComparison.Ordinal))
                    Assert(Configuration.JumpProfile(profile).Host == "2001:db8::2", type + ": IPv6 jump address is preserved.");
            }
        }
        var globalProfile = ProxyProfile.Create("Global SSH");
        globalProfile.Proxy["type"] = "SSH"; globalProfile.Proxy["host"] = "ssh.example";
        var globalEditor = new ProfileWindow(globalProfile, globalSshFingerprint: "CONNECTBOT");
        Assert(((ComboBoxItem)((ComboBox)globalEditor.FindName("SshProfileBox")).SelectedItem).Tag as string == "CONNECTBOT",
            "The editor displays the effective imported global SSH fingerprint.");
        globalEditor.Close();
        var snapshot = ConnectionSnapshot.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            connections = new[] { new { id = "ipv6", chains = new[] { "proxy" }, metadata = new
            { destinationIP = "2001:db8::1234", destinationPort = 443, network = "tcp", processPath = "ipv6.exe" } } }
        }), new Settings(), true);
        Assert(snapshot.Rows.Single().Destination == "[2001:db8::1234]:443", "Connection rows distinguish an IPv6 address from its port.");
    }
}
