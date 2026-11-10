using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WindowsMegaProxy;

public static class NotificationIdentity
{
    public static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppIdentity.Name);
        key.SetValue("DisplayName", AppIdentity.DisplayName, RegistryValueKind.ExpandString);
        using var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/{AppIdentity.Name};component/Assets/{AppIdentity.IconPrefix}app.ico"))!.Stream;
        var decoder = BitmapDecoder.Create(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First());
        using var image = new MemoryStream();
        encoder.Save(image);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.Name);
        Directory.CreateDirectory(directory);
        string iconPath = Path.Combine(directory, "notification-icon.png");
        byte[] bytes = image.ToArray();
        if (!File.Exists(iconPath) || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(iconPath, bytes);
        key.SetValue("IconUri", iconPath, RegistryValueKind.ExpandString);
    }
}
