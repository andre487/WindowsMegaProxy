using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public partial class ProfileWindow : Window
{
    private readonly ProxyProfile original;
    private readonly ProxyProfile[] otherProfiles;
    private bool initializing = true, appearanceChanged;
    public ProxyProfile? Profile { get; private set; }

    public ProfileWindow(ProxyProfile profile, IEnumerable<ProxyProfile>? profiles = null)
    {
        InitializeComponent();
        original = profile;
        otherProfiles = profiles?.Where(item => item.Id != profile.Id).ToArray() ?? [];
        NameBox.Text = profile.Data["name"]?.GetValue<string>() ?? profile.Name;
        var countries = ProfileAppearance.Countries.ToList();
        if (!countries.Any(country => country.Code == profile.CountryCode)) countries.Add(new ProfileCountry(profile.CountryCode, profile.CountryCode, -1));
        CountryBox.ItemsSource = countries;
        CountryBox.SelectedItem = countries.First(country => country.Code == profile.CountryCode);
        ColorBox.ItemsSource = new[] { new ProfileColor(-1, Ui.T("Automatic (country flag)"), "Transparent") }.Concat(ProfileAppearance.Colors).ToArray();
        bool automatic = profile.Data["color"] is null || profile.Data["windows"] is System.Text.Json.Nodes.JsonObject options
            && options["colorMode"] is System.Text.Json.Nodes.JsonValue mode && mode.TryGetValue<string>(out var value) && value == "auto";
        ColorBox.SelectedIndex = automatic ? 0 : profile.ColorIndex + 1;
        if (!TypeBox.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == profile.Type))
            TypeBox.Items.Add(new ComboBoxItem { Content = profile.Type, Tag = profile.Type });
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == profile.Type);
        HostBox.Text = profile.Host; PortBox.Text = profile.Port.ToString();
        UsernameBox.Text = profile.Proxy["username"]?.GetValue<string>() ?? "";
        PasswordBox.Password = profile.Proxy["password"]?.GetValue<string>() ?? "";
        InvalidCertificateBox.IsChecked = profile.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false;
        HttpVersionBox.SelectedIndex = profile.PreferHttp3 ? 2 : profile.PreferHttp2 ? 1 : 0;
        var jump = profile.Proxy["jump"] as System.Text.Json.Nodes.JsonObject;
        JumpHostBox.Text = jump?["host"]?.GetValue<string>() ?? "";
        JumpPortBox.Text = (jump?["port"]?.GetValue<int>() ?? (profile.Type == "SSH_JUMP" ? 22 : 443)).ToString();
        JumpPrivateKeyBox.Text = jump?["privateKey"]?.GetValue<string>() ?? "";
        SameJumpAuthenticationBox.IsChecked = jump?["sameAuthentication"]?.GetValue<bool>() ?? true;
        JumpUsernameBox.Text = jump?["username"]?.GetValue<string>() ?? "";
        JumpPasswordBox.Password = jump?["password"]?.GetValue<string>() ?? "";
        JumpInvalidCertificateBox.IsChecked = jump?["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false;
        SameJumpAuthenticationChanged(this, null!);
        PrivateKeyBox.Text = profile.Proxy["privateKey"]?.GetValue<string>() ?? "";
        string sshProfile = profile.Proxy["sshProfile"]?.GetValue<string>() ?? "DEFAULT";
        if (!SshConfiguration.Profiles.Contains(sshProfile)) SshProfileBox.Items.Add(new ComboBoxItem { Content = sshProfile, Tag = sshProfile });
        SshProfileBox.SelectedItem = SshProfileBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == sshProfile);
        AuthProbeHostBox.Text = ProxyAuthenticationProbe.Host(profile);
        AllowIpv6Box.IsChecked = profile.AllowIpv6;
        foreach (var provider in ProfileDns.Providers)
            DnsProviderBox.Items.Add(new ComboBoxItem { Content = Ui.T(provider.Name), Tag = provider.Id });
        string dnsProvider = ProfileDns.Provider(profile);
        if (!DnsProviderBox.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == dnsProvider))
            DnsProviderBox.Items.Add(new ComboBoxItem { Content = dnsProvider, Tag = dnsProvider });
        DnsProviderBox.SelectedItem = DnsProviderBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == dnsProvider);
        DnsUrlBox.Text = ProfileDns.CustomUrl(profile);
        AuthProbeHostPanel.Visibility = profile.Type == "HTTPS" ? Visibility.Visible : Visibility.Collapsed;
        TypeChanged(this, null!);
        ConnectionFields.IsEnabled = profile.Supported;
        SupportText.Text = profile.Supported
            ? Ui.T("Other clients' settings are preserved. SOCKS5 does not encrypt the server connection; UDP requires server support.")
            : profile.Availability + Ui.T(". You can change the name, country and color; connection parameters will be preserved.");
        initializing = false;
    }

    private void DnsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DnsUrlPanel is not null)
            DnsUrlPanel.Visibility = (DnsProviderBox.SelectedItem as ComboBoxItem)?.Tag as string == "CUSTOM" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CountryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) appearanceChanged = true;
    }

    private void ColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing) appearanceChanged = true;
    }

    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        string? type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        bool ssh = type is "SSH" or "SSH_JUMP";
        bool jump = type is "HTTPS_JUMP" or "SSH_JUMP";
        if (JumpPrivateKeyPanel is not null) JumpPrivateKeyPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        if (JumpInvalidCertificateBox is not null) JumpInvalidCertificateBox.Visibility = ssh ? Visibility.Collapsed : Visibility.Visible;
        if (JumpFields is not null) JumpFields.Visibility = jump ? Visibility.Visible : Visibility.Collapsed;
        if (SshFields is not null) SshFields.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        if (!initializing && PortBox is not null)
        {
            if (ssh && PortBox.Text == "443") PortBox.Text = "22";
            else if (!ssh && PortBox.Text == "22") PortBox.Text = "443";
            if (ssh && JumpPortBox.Text == "443") JumpPortBox.Text = "22";
            else if (!ssh && JumpPortBox.Text == "22") JumpPortBox.Text = "443";
        }
        if (InvalidCertificateBox is not null)
        {
            InvalidCertificateBox.Visibility = ssh ? Visibility.Collapsed : Visibility.Visible;
            InvalidCertificateBox.IsEnabled = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string is "HTTPS" or "HTTPS_JUMP" or "MASQUE";
        }
        if (AuthProbeHostPanel is not null)
            AuthProbeHostPanel.Visibility = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "HTTPS" ? Visibility.Visible : Visibility.Collapsed;
        if (HttpVersionPanel is not null)
            HttpVersionPanel.Visibility = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string is "HTTPS" or "HTTPS_JUMP" ? Visibility.Visible : Visibility.Collapsed;
        if (Http3HelpText is not null)
            Http3HelpText.Visibility = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string is "HTTPS" or "HTTPS_JUMP" or "MASQUE" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            var candidate = original.Copy();
            candidate.Data["name"] = NameBox.Text.Trim();
            candidate.Data["countryCode"] = ((ProfileCountry)CountryBox.SelectedItem).Code;
            if (appearanceChanged || original.Data["color"] is null)
            {
                var selected = (ProfileColor)ColorBox.SelectedItem;
                candidate.Data["color"] = selected.Index >= 0 ? selected.Index
                    : ProfileAppearance.ColorForCountry(candidate.CountryCode, original.Data["color"] is null ? ProfileAppearance.NextColor(otherProfiles) : original.ColorIndex);
                var options = candidate.Data["windows"] as System.Text.Json.Nodes.JsonObject;
                if (options is null) { options = new(); candidate.Data["windows"] = options; }
                options["colorMode"] = selected.Index >= 0 ? "manual" : "auto";
            }
            if (candidate.Supported)
            {
                if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
                {
                    InvalidField(PortBox, "Invalid port.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(HostBox.Text))
                {
                    InvalidField(HostBox, "Enter a proxy host.");
                    return;
                }
                var type = (string)((ComboBoxItem)TypeBox.SelectedItem).Tag;
                if (candidate.Type != type && candidate.Proxy["privateKey"] is not null)
                    candidate.Proxy.Remove("privateKey");
                candidate.Proxy["type"] = type;
                candidate.Proxy["host"] = HostBox.Text.Trim();
                candidate.Proxy["port"] = port;
                candidate.Proxy["username"] = UsernameBox.Text;
                candidate.Proxy["password"] = PasswordBox.Password;
                if (type is "SSH" or "SSH_JUMP")
                {
                    candidate.Proxy["privateKey"] = PrivateKeyBox.Text;
                    candidate.Proxy["sshProfile"] = (string)((ComboBoxItem)SshProfileBox.SelectedItem).Tag;
                    SshConfiguration.Build(candidate, "validation");
                }
                if (type == "HTTPS") { candidate.Proxy["knockHost"] = AuthProbeHostBox.Text.Trim(); candidate.Proxy.Remove("authProbeHost"); }
                if (type is "HTTPS" or "HTTPS_JUMP")
                {
                    candidate.Proxy["preferHttp2"] = HttpVersionBox.SelectedIndex >= 1;
                    candidate.Proxy["preferHttp3"] = HttpVersionBox.SelectedIndex == 2;
                }
                if (type is "HTTPS_JUMP" or "SSH_JUMP")
                {
                    if (!int.TryParse(JumpPortBox.Text, out int jumpPort) || jumpPort is < 1 or > 65535)
                    {
                        InvalidField(JumpPortBox, "Invalid port.");
                        return;
                    }
                    var options = candidate.Proxy["jump"] as System.Text.Json.Nodes.JsonObject ?? new();
                    options["host"] = JumpHostBox.Text.Trim(); options["port"] = jumpPort;
                    options["sameAuthentication"] = SameJumpAuthenticationBox.IsChecked == true;
                    options["username"] = JumpUsernameBox.Text; options["password"] = JumpPasswordBox.Password;
                    if (type == "SSH_JUMP")
                    {
                        options["privateKey"] = JumpPrivateKeyBox.Text;
                    }
                    bool invalidJump = type == "HTTPS_JUMP" && JumpInvalidCertificateBox.IsChecked == true;
                    if (invalidJump && !(original.Proxy["jump"]?["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false) &&
                        !MessageDialog.Confirm(this, Ui.T("Disable jump certificate verification? This weakens connection security."), Ui.T("Proxy certificate"))) return;
                    options["allowInvalidProxyCertificate"] = invalidJump;
                    if (candidate.Proxy["jump"] is null) candidate.Proxy["jump"] = options;
                    if (type == "SSH_JUMP") SshConfiguration.Build(Configuration.JumpProfile(candidate), "validation");
                }
                bool insecure = type is "HTTPS" or "HTTPS_JUMP" or "MASQUE" && InvalidCertificateBox.IsChecked == true;
                if (insecure && !(original.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false) &&
                    !MessageDialog.Confirm(this, Ui.T("Disable proxy certificate verification? This weakens connection security."), Ui.T("Proxy certificate"))) return;
                candidate.Proxy["allowInvalidProxyCertificate"] = insecure;
            }
            string provider = (string)((ComboBoxItem)DnsProviderBox.SelectedItem).Tag;
            if ((AllowIpv6Box.IsChecked == true) != original.AllowIpv6)
            {
                candidate.Data["routing"] ??= new System.Text.Json.Nodes.JsonObject();
                candidate.Data["routing"]!["allowIpv6"] = AllowIpv6Box.IsChecked == true;
            }
            if (provider != ProfileDns.Provider(original) || DnsUrlBox.Text.Trim() != ProfileDns.CustomUrl(original))
            {
                var dns = candidate.Data["dns"] as System.Text.Json.Nodes.JsonObject;
                if (dns is null) { dns = new(); candidate.Data["dns"] = dns; }
                dns["provider"] = provider;
                dns["customDohUrl"] = DnsUrlBox.Text.Trim();
                ProfileDns.Build(candidate);
            }
            PortableConfiguration.ValidateProfile(candidate);
            Profile = candidate;
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException) { ErrorText.Text = error.Message; }
    }

    private void InvalidField(TextBox field, string message)
    {
        ErrorText.Text = Ui.T(message);
        field.Focus();
        field.SelectAll();
        field.BringIntoView();
    }

    private void SameJumpAuthenticationChanged(object sender, RoutedEventArgs e)
    {
        if (JumpCredentials is not null) JumpCredentials.IsEnabled = SameJumpAuthenticationBox.IsChecked != true;
    }

    private void LoadPrivateKeyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Ui.T("SSH private key|*.*"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 65536) throw new InvalidDataException(Ui.T("The private key exceeds the 64 KiB limit."));
            PrivateKeyBox.Text = File.ReadAllText(dialog.FileName);
            ErrorText.Text = "";
        }
        catch (Exception error) { ErrorText.Text = error.Message; }
    }
}
