using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using WindowsMegaProxy;

internal static class ExistingTcpChecks
{
    public static void Run()
    {
        var settings = new Settings { Rules = [new() { Source = "folder", Path = @"C:\Apps", Action = "proxy" }] };
        bool Match(string path = @"C:\Apps\nested\app.exe", string ip = "1.1.1.1") => ExistingTcpConnections.ShouldClose(settings, path, IPAddress.Parse(ip));
        void Require(bool value) { if (!value) throw new Exception("Existing TCP policy regression."); }
        Require(!Match());
        settings.ResetExistingTcpConnections = true;
        Require(Match() && !Match(@"C:\AppsOther\app.exe") && !Match(ip: "127.0.0.1") && !Match(ip: "192.168.1.2"));
        Require(!Match(@"C:\Apps\WindowsMegaProxy.Core.exe") && !Match(@"C:\Apps\WindowsMegaProxy.Dev.Core.exe"));
        Require(Match(@"C:\Apps\sing-box.exe"));
        settings.Rules[0].Source = "exe"; settings.Rules[0].Path = @"C:\Apps\nested\app.exe";
        Require(Match() && !Match(@"C:\Apps\nested\other.exe"));
        settings.Rules[0].Source = "folder"; settings.Rules[0].Path = @"C:\Apps";
        settings.Rules.Insert(0, new() { Action = "system", Destination = "example.com" });
        Require(!Match());
        settings.Rules[0].Enabled = false;
        Require(Match());
        settings.Rules[0].Enabled = true;
        settings.Rules[0].Destination = "8.8.8.0/24";
        Require(Match() && !Match(ip: "8.8.8.8"));
        settings.Rules[1].Action = "block";
        Require(!Match());
        settings.Rules.Clear(); settings.DefaultAction = "proxy";
        Require(!Match());
        var profile = ProxyProfile.Create("TCP reset test");
        profile.Proxy["host"] = "example.com";
        settings.Profiles.Add(profile); settings.ActiveProfileId = profile.Id;
        var exported = PortableConfiguration.Export(settings);
        var imported = PortableConfiguration.Import(exported.ToJsonString(), new Settings());
        Require(imported.ResetExistingTcpConnections);
        string absent = exported.ToJsonString().Replace("\"resetExistingTcpConnections\":true,", "");
        Require(!absent.Contains("resetExistingTcpConnections"));
        Require(PortableConfiguration.Import(absent, settings).ResetExistingTcpConnections);
        Require(!PortableConfiguration.Import(exported.ToJsonString().Replace("\"resetExistingTcpConnections\":true", "\"resetExistingTcpConnections\":false"), settings).ResetExistingTcpConnections);

        // Read the native table without closing anything on the developer's machine.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using var server = listener.AcceptTcpClient();
        Require(ExistingTcpConnections.Read().Any(row => row.Pid == Environment.ProcessId && row.State == 5));
        Console.WriteLine("Existing TCP rule selection, settings round-trip and native table checks passed; no connections were closed.");
    }
}
