using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public static partial class Ui
{
    public static void Apply(string language, string theme, Window window)
    {
        SetLanguage(language);
        foreach (var pair in Russian) Application.Current.Resources[pair.Key] = T(pair.Key);
#pragma warning disable WPF0001
        var mode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
        Application.Current.ThemeMode = mode;
        window.ThemeMode = mode;
#pragma warning restore WPF0001
        WindowInput.Initialize();
    }
}
