using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsMegaProxy;

public sealed class MachineSettingsStore
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsMegaProxy");
    public string DirectoryPath { get; }
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsMegaProxy.GlobalSettings.v1");
    private readonly bool machine;
    public bool Initialized => File.Exists(FilePath);

    public MachineSettingsStore(string? directory = null, bool machine = true)
    {
        this.machine = machine;
        DirectoryPath = Path.GetFullPath(directory ?? DefaultDirectory);
        var info = System.IO.Directory.CreateDirectory(DirectoryPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The service data directory cannot be a reparse point.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = (SecurityIdentifier)info.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        if (machine && owner != system && owner != administrators)
            throw new UnauthorizedAccessException("The service data directory has an untrusted owner.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(machine ? system : WindowsIdentity.GetCurrent().User!);
        foreach (var sid in new[] { system, administrators, machine ? system : WindowsIdentity.GetCurrent().User! })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        foreach (string child in new[] { "runtime", "logs" })
        {
            var subdirectory = System.IO.Directory.CreateDirectory(Path.Combine(DirectoryPath, child));
            if ((subdirectory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Service data cannot contain reparse points.");
            CheckOwner(subdirectory.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
            subdirectory.SetAccessControl(security);
        }
    }

    public Settings Load()
    {
        if (!Initialized) return new Settings { Language = "en" };
        if ((File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(FilePath).Length > 16 * 1024 * 1024)
            throw new IOException("Invalid global settings file.");
        CheckOwner(new FileInfo(FilePath).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        var envelope = JsonNode.Parse(File.ReadAllText(FilePath))!;
        if (envelope["version"]?.GetValue<int>() != 1) throw new IOException("Unsupported global settings version.");
        var clear = ProtectedData.Unprotect(Convert.FromBase64String(envelope["protectedSettings"]!.GetValue<string>()), Entropy, DataProtectionScope.CurrentUser);
        var settings = JsonSerializer.Deserialize<Settings>(clear)
            ?? throw new IOException("Empty global settings.");
        PortableConfiguration.ValidateSettings(settings);
        return settings;
    }

    public void Save(Settings settings)
    {
        PortableConfiguration.ValidateSettings(settings);
        string data = JsonSerializer.Serialize(settings, Configuration.JsonOptions);
        if (Encoding.UTF8.GetByteCount(data) > 8 * 1024 * 1024) throw new IOException("Global settings exceed 8 MiB.");
        // CurrentUser is the service identity (LocalSystem), never a connected desktop user's identity.
        var envelope = new JsonObject { ["version"] = 1, ["protectedSettings"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(data), Entropy, DataProtectionScope.CurrentUser)) };
        SettingsStore.WriteAtomic(FilePath, envelope.ToJsonString(Configuration.JsonOptions));
    }

    private void CheckOwner(IdentityReference? owner)
    {
        if (machine && (owner is not SecurityIdentifier sid || !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new UnauthorizedAccessException("Service data has an untrusted owner.");
    }
}
