using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public static class UiTheme
{
    public static Style ExplanationStyle { get; } = CreateExplanationStyle();

    private static Style CreateExplanationStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Normal));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
        return style;
    }

    private static string? appliedLanguage;
    private static ResourceDictionary? localizedResources;
    public static void Apply(string language, string theme, Window window)
    {
        if (appliedLanguage != language || Ui.Language != language)
        {
            Ui.SetLanguage(language);
            var resources = new ResourceDictionary();
            foreach (var pair in Ui.Russian) resources[pair.Key] = Ui.T(pair.Key);
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            int index = localizedResources is null ? -1 : dictionaries.IndexOf(localizedResources);
            if (index < 0) dictionaries.Add(resources);
            else dictionaries[index] = resources;
            localizedResources = resources;
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
