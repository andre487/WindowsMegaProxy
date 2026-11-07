using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public static class UiTheme
{
    private static string? appliedLanguage;
    public static void Apply(string language, string theme, Window window)
    {
        if (appliedLanguage != language || Ui.Language != language)
        {
            Ui.SetLanguage(language);
            foreach (var pair in Ui.Russian) Application.Current.Resources[pair.Key] = Ui.T(pair.Key);
            appliedLanguage = language;
        }
#pragma warning disable WPF0001
        var mode = theme switch { "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => ThemeMode.System };
        if (Application.Current.ThemeMode != mode) Application.Current.ThemeMode = mode;
        if (window.ThemeMode != mode) window.ThemeMode = mode;
#pragma warning restore WPF0001
        WindowInput.Initialize();
    }
}
