using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public static class SettingsStore
{
    public static string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.Name);
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static void PrepareDirectory()
    {
        var directory = Directory.CreateDirectory(DirectoryPath);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    public static Settings Load()
    {
        PrepareDirectory();
        if (!File.Exists(FilePath)) return new Settings();
        var document = JsonNode.Parse(File.ReadAllText(FilePath))?.AsObject() ?? throw new InvalidDataException(Ui.T("Empty settings."));
        Settings settings;
        if (document["protectedSettings"] is JsonValue encrypted)
            settings = JsonSerializer.Deserialize<Settings>(Unprotect(encrypted.GetValue<string>())) ?? throw new InvalidDataException(Ui.T("Empty settings."));
        else
        {
            // Migrate the original single-server settings without changing them until the next save.
            settings = new Settings
            {
                DefaultAction = document["DefaultAction"]?.GetValue<string>() ?? "system",
                BypassLan = document["BypassLan"]?.GetValue<bool>() ?? true,
                AutoConnect = document["AutoConnect"]?.GetValue<bool>() ?? false,
                Rules = document["Rules"]?.Deserialize<System.Collections.Generic.List<RoutingRule>>() ?? []
            };
            var host = document["Host"]?.GetValue<string>() ?? "";
            if (host.Length > 0)
            {
                var profile = ProxyProfile.Create(Ui.T("My proxy"));
                profile.Proxy["type"] = (document["ProxyType"]?.GetValue<string>() ?? "socks") switch
                {
                    "socks" => "SOCKS5",
                    "http" => "HTTP",
                    "https" => "HTTPS",
                    _ => throw new InvalidDataException(Ui.T("Unknown legacy profile type."))
                };
                profile.Proxy["host"] = host;
                profile.Proxy["port"] = document["Port"]?.GetValue<int>() ?? 1080;
                profile.Proxy["username"] = document["Username"]?.GetValue<string>() ?? "";
                profile.Proxy["password"] = Unprotect(document["ProtectedPassword"]?.GetValue<string>() ?? "");
                settings.Profiles.Add(profile);
                settings.ActiveProfileId = profile.Id;
            }
        }
        PortableConfiguration.ValidateSettings(settings);
        return settings;
    }

    public static void Save(Settings settings)
    {
        PortableConfiguration.ValidateSettings(settings);
        var serialized = JsonSerializer.Serialize(settings, Configuration.JsonOptions);
        if (Encoding.UTF8.GetByteCount(serialized) > 8 * 1024 * 1024) throw new InvalidDataException(Ui.T("Local settings exceed 8 MiB."));
        var envelope = new JsonObject { ["version"] = 1, ["protectedSettings"] = Protect(serialized) };
        WriteAtomic(FilePath, envelope.ToJsonString(Configuration.JsonOptions));
    }

    public static void WriteAtomic(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Protect(string text) => text.Length == 0 ? "" : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string text) => text.Length == 0 ? "" : Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser));
}
