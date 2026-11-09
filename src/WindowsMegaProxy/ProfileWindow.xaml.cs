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
        JumpPortBox.Text = (jump?["port"]?.GetValue<int>() ?? 443).ToString();
        SameJumpAuthenticationBox.IsChecked = jump?["sameAuthentication"]?.GetValue<bool>() ?? true;
        JumpUsernameBox.Text = jump?["username"]?.GetValue<string>() ?? "";
        JumpPasswordBox.Password = jump?["password"]?.GetValue<string>() ?? "";
        JumpInvalidCertificateBox.IsChecked = jump?["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false;
        SameJumpAuthenticationChanged(this, null!);
        PrivateKeyBox.Text = profile.Proxy["privateKey"]?.GetValue<string>() ?? "";
        SshHostKeyBox.Text = SshConfiguration.PublicKey(profile);
        SshFingerprintText.Text = profile.Proxy["trustedHostKey"]?.GetValue<string>() ?? "";
        AcceptAnyHostKeyBox.IsChecked = profile.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false;
        string sshProfile = profile.Proxy["sshProfile"]?.GetValue<string>() ?? "DEFAULT";
        if (!SshConfiguration.Profiles.Contains(sshProfile)) SshProfileBox.Items.Add(new ComboBoxItem { Content = sshProfile, Tag = sshProfile });
        SshProfileBox.SelectedItem = SshProfileBox.Items.Cast<ComboBoxItem>().First(item => (string)item.Tag == sshProfile);
        AuthProbeHostBox.Text = ProxyAuthenticationProbe.Host(profile);
        AuthProbeHostPanel.Visibility = profile.Type == "HTTPS" ? Visibility.Visible : Visibility.Collapsed;
        TypeChanged(this, null!);
        ConnectionFields.IsEnabled = profile.Supported;
        SupportText.Text = profile.Supported
            ? Ui.T("Other clients' settings are preserved. SOCKS5 does not encrypt the server connection; UDP requires server support.")
            : profile.Availability + Ui.T(". You can change the name, country and color; connection parameters will be preserved.");
        initializing = false;
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
        bool ssh = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "SSH";
        bool jump = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "HTTPS_JUMP";
        if (JumpFields is not null) JumpFields.Visibility = jump ? Visibility.Visible : Visibility.Collapsed;
        if (SshFields is not null) SshFields.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        if (!initializing && PortBox is not null)
        {
            if (ssh && PortBox.Text == "443") PortBox.Text = "22";
            else if (!ssh && PortBox.Text == "22") PortBox.Text = "443";
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
                if (type == "SSH")
                {
                    candidate.Proxy["privateKey"] = PrivateKeyBox.Text;
                    candidate.Proxy["sshProfile"] = (string)((ComboBoxItem)SshProfileBox.SelectedItem).Tag;
                    bool acceptAny = AcceptAnyHostKeyBox.IsChecked == true;
                    if (acceptAny && !(original.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false) &&
                        MessageBox.Show(this, Ui.T("Accept any SSH server key? The server identity will not be verified."), "SSH", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    candidate.Proxy["acceptAnyHostKey"] = acceptAny;
                    string key = SshHostKeyBox.Text.Trim();
                    if (key.Length > 0)
                    {
                        if (candidate.Data["windows"] is not System.Text.Json.Nodes.JsonObject) candidate.Data["windows"] = new System.Text.Json.Nodes.JsonObject();
                        candidate.Data["windows"]!["sshHostKey"] = key;
                        string actual = SshConfiguration.Fingerprint(key);
                        string trusted = original.Proxy["trustedHostKey"]?.GetValue<string>() ?? "";
                        if (trusted.Length > 0 && trusted != actual && MessageBox.Show(this,
                            Ui.F("Replace the trusted SSH fingerprint?\nPrevious: {0}\nNew: {1}", trusted, actual), "SSH", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                        candidate.Proxy["trustedHostKey"] = actual;
                    }
                    else if (candidate.Data["windows"] is System.Text.Json.Nodes.JsonObject windows) windows.Remove("sshHostKey");
                    SshConfiguration.Build(candidate, "validation", allowUnresolvedHostKey: true);
                }
                if (type == "HTTPS") { candidate.Proxy["knockHost"] = AuthProbeHostBox.Text.Trim(); candidate.Proxy.Remove("authProbeHost"); }
                if (type is "HTTPS" or "HTTPS_JUMP")
                {
                    candidate.Proxy["preferHttp2"] = HttpVersionBox.SelectedIndex >= 1;
                    candidate.Proxy["preferHttp3"] = HttpVersionBox.SelectedIndex == 2;
                }
                if (type == "HTTPS_JUMP")
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
                    bool invalidJump = JumpInvalidCertificateBox.IsChecked == true;
                    if (invalidJump && !(original.Proxy["jump"]?["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false) &&
                        MessageBox.Show(this, Ui.T("Disable jump certificate verification? This weakens connection security."), Ui.T("Proxy certificate"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    options["allowInvalidProxyCertificate"] = invalidJump;
                    if (candidate.Proxy["jump"] is null) candidate.Proxy["jump"] = options;
                }
                bool insecure = type is "HTTPS" or "HTTPS_JUMP" or "MASQUE" && InvalidCertificateBox.IsChecked == true;
                if (insecure && !(original.Proxy["allowInvalidProxyCertificate"]?.GetValue<bool>() ?? false) &&
                    MessageBox.Show(this, Ui.T("Disable proxy certificate verification? This weakens connection security."), Ui.T("Proxy certificate"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                candidate.Proxy["allowInvalidProxyCertificate"] = insecure;
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

    private void SshHostKeyChanged(object sender, TextChangedEventArgs e)
    {
        if (SshFingerprintText is null) return;
        try { SshFingerprintText.Text = SshHostKeyBox.Text.Trim().Length == 0 ? "" : SshConfiguration.Fingerprint(SshHostKeyBox.Text.Trim()); }
        catch (ArgumentException) { SshFingerprintText.Text = Ui.T("Enter an OpenSSH server public key."); }
    }

    private async void ReadSshHostKeyClick(object sender, RoutedEventArgs e)
    {
        ReadSshHostKeyButton.IsEnabled = false;
        try
        {
            if (!int.TryParse(PortBox.Text, out int port)) throw new ArgumentException(Ui.T("Invalid port."));
            SshHostKeyBox.Text = await SshConfiguration.ReadHostKeyAsync(HostBox.Text.Trim(), port,
                original.Proxy["trustedHostKey"]?.GetValue<string>() ?? "", System.Threading.CancellationToken.None);
            ErrorText.Text = "";
        }
        catch (Exception error) { ErrorText.Text = error is OperationCanceledException ? Ui.T("Could not read the SSH server key.") : error.Message; }
        finally { ReadSshHostKeyButton.IsEnabled = true; }
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
