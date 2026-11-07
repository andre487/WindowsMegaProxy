using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WindowsMegaProxy;

// DataGrid columns are outside the visual tree and need explicit language notifications.
public sealed class UiStrings : INotifyPropertyChanged
{
    public string this[string key] => Ui.T(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

public static class Ui
{
    public static UiStrings Strings { get; } = new();
    public static IReadOnlyDictionary<string, string> Russian { get; } = ReadStrings();
    public static CultureInfo WindowsLanguage { get; } = CultureInfo.CurrentUICulture;
    public static string Language { get; private set; } = DefaultLanguage(WindowsLanguage);
    public static string DefaultLanguage(CultureInfo windowsLanguage) => windowsLanguage.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
    public static string T(string english) => Language == "ru" && Russian.TryGetValue(english, out var translated) ? translated : english;
    public static string F(string english, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, T(english), arguments);

    public static void SetLanguage(string language)
    {
        if (language is not ("en" or "ru")) throw new ArgumentException("Unsupported application language.");
        Language = language;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        Strings.Refresh();
    }

    private static IReadOnlyDictionary<string, string> ReadStrings()
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream("WindowsMegaProxy.Strings.json")
            ?? throw new InvalidDataException("Missing UI translations.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
