using System;
using System.Linq;
using System.Windows;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private void UpdateConnectionDetails()
    {
        bool unavailable = service is not null && !serviceAvailable;
        bool connected = !unavailable && engine?.Running == true;
        var profile = connected && engine!.HasProbeProfile ? engine.AppliedSettings?.ActiveProfile : null;
        string protocol = profile?.Type switch
        {
            "HTTPS" or "HTTPS_JUMP" or "MASQUE" when engine!.Http3Selected => "HTTP/3 · QUIC",
            "HTTPS" or "HTTPS_JUMP" when profile.PreferHttp2 => "",
            "HTTPS" or "HTTPS_JUMP" or "HTTP" => "HTTP/1.1",
            "SOCKS5" => "SOCKS5",
            "SSH" or "SSH_JUMP" => "SSH",
            _ => ""
        };
        TransportText.Text = protocol.Length == 0 ? "" : "(" + protocol + ")";
        TransportText.Visibility = protocol.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (tray is null) return;
        bool connecting = connection.State == ConnectionState.Connecting;
        if (connecting) profile = null;
        bool checking = settingsLoading && (service is null || serviceInstance is null);
        string heading = connecting ? BrandLabel("MegaProxy") + " \u2014 " + Ui.T("Connecting…")
            : connected ? BrandLabel("MegaProxy") : checking ? BrandLabel("MegaProxy") + " \u2014 " + Ui.T("Checking connection\u2026")
            : BrandLabel(unavailable && ServiceUpdateInProgress ? "MegaProxy \u2014 update in progress" : unavailable ? "MegaProxy \u2014 service unavailable" : "MegaProxy \u2014 disconnected");
        if (connected && !connecting && profile is null) heading += " \u2014 " + Ui.T("Connected");
        string suffix = profile is null ? "" : " [" + profile.TypeLabel + "]";
        if (profile is not null && protocol.Length > 0 && protocol is not ("SSH" or "SOCKS5")) suffix += "\n" + protocol;
        string name = profile is null ? "" : new string(profile.Name.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        // NotifyIcon limits tooltip text to 127 UTF-16 code units; preserve type and protocol.
        int limit = 127 - heading.Length - suffix.Length - 3;
        if (name.Length > limit)
        {
            int length = limit - 1;
            if (char.IsHighSurrogate(name[length - 1])) length--;
            name = name[..length] + "\u2026";
        }
        string text = heading + (profile is null ? "" : " \u2014 " + name) + suffix;
        if (tray.Text != text) tray.Text = text;
    }
}
