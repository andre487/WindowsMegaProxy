using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsMegaProxy;

internal static class RebootFilesChecks
{
    public static void Run()
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        // Build the actual component names, including duplicated core entries in the app archive.
        byte[] Build(string path, string duplicateHash) => System.Text.Encoding.UTF8.GetBytes(
            "{\"components\":{\"app\":{\"files\":[" +
            JsonSerializer.Serialize(new { path = AppIdentity.ServiceExecutable, size = 3, sha256 = hash }) + "," +
            JsonSerializer.Serialize(new { path, size = 3, sha256 = hash }) + "]},\"sing-box\":{\"files\":[" +
            JsonSerializer.Serialize(new { path = AppIdentity.ServiceExecutable, size = 3, sha256 = duplicateHash }) + "]},\"sing-box-h3\":{\"files\":[]}}}");
        var files = Installation.RebootFiles(Build("WindowsMegaProxy.Runtime.dll", hash));
        if (files.Length != 2) throw new Exception("Duplicate payload files must be verified and scheduled once.");
        foreach (string path in new[] { "../escape.dll", "C:/escape.dll", "update/setup.exe", "dir/../escape.dll", "dir/file:stream", "dir/CON.dll", "file.", "user-installation.sid", "unins000.exe" })
        {
            try { Installation.RebootFiles(Build(path, hash)); throw new Exception("Unsafe payload path was accepted: " + path); }
            catch (InvalidDataException) { }
        }
        try { Installation.RebootFiles(Build("WindowsMegaProxy.Runtime.dll", new string('0', 64))); throw new Exception("Conflicting file hashes were accepted."); }
        catch (InvalidDataException) { }
        string root = Path.Combine(Path.GetTempPath(), "MegaProxy.RebootFiles." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in files) File.WriteAllBytes(Path.Combine(root, file.Path), [1, 2, 3]);
            Installation.VerifyRebootFiles(root, files);
            File.WriteAllBytes(Path.Combine(root, files[0].Path), [3, 2, 1]);
            try { Installation.VerifyRebootFiles(root, files); throw new Exception("Same-size payload corruption was accepted."); }
            catch (CryptographicException) { }
            if (!Installation.ContainsQueuedRebootFiles(root, [@"!\??\" + root + @"\file.dll"]) ||
                Installation.ContainsQueuedRebootFiles(root, [@"!\??\" + root + @"-other\file.dll"]))
                throw new Exception("Pending replacement detection must stay within this installation.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Reboot payload path validation, deduplication, hashes and queue isolation passed.");
    }
}
