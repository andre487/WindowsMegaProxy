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
            "SSH" => "SSH",
            _ => ""
        };
        TransportText.Text = protocol;
        TransportText.Visibility = protocol.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (tray is null) return;
        string heading = BrandLabel(unavailable ? "MegaProxy — service unavailable" : connected ? "MegaProxy — TUN connected" : "MegaProxy — disconnected");
        string suffix = protocol.Length == 0 ? "" : "\n" + protocol;
        string name = profile is null ? "" : new string(profile.Name.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        // NotifyIcon limits tooltip text to 127 UTF-16 code units; preserve the protocol line.
        int limit = 127 - heading.Length - suffix.Length - 1;
        if (name.Length > limit)
        {
            int length = limit - 1;
            if (char.IsHighSurrogate(name[length - 1])) length--;
            name = name[..length] + "…";
        }
        string text = heading + (name.Length == 0 ? "" : "\n" + name) + suffix;
        if (tray.Text != text) tray.Text = text;
    }
}
