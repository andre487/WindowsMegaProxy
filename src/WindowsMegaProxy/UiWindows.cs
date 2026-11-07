using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public static class UiTheme
{
    public static void Apply(string language, string theme, Window window)
    {
        Ui.SetLanguage(language);
        foreach (var pair in Ui.Russian) Application.Current.Resources[pair.Key] = Ui.T(pair.Key);
#pragma warning disable WPF0001
        var mode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
        Application.Current.ThemeMode = mode;
        window.ThemeMode = mode;
#pragma warning restore WPF0001
        WindowInput.Initialize();
    }
}
