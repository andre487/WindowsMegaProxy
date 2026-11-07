using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WindowsMegaProxy;

public sealed record ProfileColor(int Index, string Name, string Hex);

public sealed record ProfileCountry(string Code, string Name, int PreferredColor)
{
    public string Label => Code.Length == 0 ? Name : $"{Name} ({Code})";
    public string? FlagUri => Code.Length > 0 && ProfileAppearance.Countries.Any(country => country.Code == Code)
        ? $"pack://application:,,,/WindowsMegaProxy;component/Assets/Flags/{Code}.png" : null;
}

public static class ProfileAppearance
{
    private static readonly ProfileColor[] Palette = Read<ProfileColor[]>("ProfileColors.json");
    private static readonly ProfileCountry[] CountryCatalog = Read<ProfileCountry[]>("Countries.json");
    public static IReadOnlyList<ProfileColor> Colors => Palette.Select(color => color with { Name = Ui.T(color.Name) }).ToArray();
    private static readonly Dictionary<string, ProfileCountry[]> LocalizedCountries = new();
    public static IReadOnlyList<ProfileCountry> Countries => LocalizedCountries.TryGetValue(Ui.Language, out var countries) ? countries
        : LocalizedCountries[Ui.Language] = CountryCatalog
        .Select(country => country with { Name = CountryName(country.Code, country.Name) })
        .OrderBy(country => country.Name, StringComparer.CurrentCulture)
        .Prepend(new ProfileCountry("", Ui.T("Not specified"), -1)).ToArray();

    public static ProfileCountry Country(string code) => Countries.FirstOrDefault(country => country.Code == code)
        ?? new ProfileCountry("", code, -1);

    public static int NextColor(IEnumerable<ProxyProfile> profiles)
    {
        var counts = new int[Colors.Count];
        foreach (var profile in profiles) counts[profile.ColorIndex]++;
        return Enumerable.Range(0, counts.Length).OrderBy(index => counts[index]).ThenBy(index => index).First();
    }

    public static int ColorForCountry(string code, int fallback)
    {
        int preferred = Country(code).PreferredColor;
        return preferred >= 0 ? preferred : fallback;
    }

    public static void AssignMissingColors(IEnumerable<ProxyProfile> profiles)
    {
        var list = profiles.ToList();
        var assigned = list.Where(profile => profile.Data["color"] is not null).ToList();
        foreach (var profile in list.Where(profile => profile.Data["color"] is null))
        {
            profile.Data["color"] = ColorForCountry(profile.CountryCode, NextColor(assigned));
            if (profile.Data["windows"] is null) profile.Data["windows"] = new System.Text.Json.Nodes.JsonObject();
            if (profile.Data["windows"] is System.Text.Json.Nodes.JsonObject options && !options.ContainsKey("colorMode")) options["colorMode"] = "auto";
            assigned.Add(profile);
        }
    }

    private static T Read<T>(string name)
    {
        using var stream = typeof(ProfileAppearance).Assembly.GetManifestResourceStream("WindowsMegaProxy." + name)
            ?? throw new InvalidDataException(Ui.T("The embedded profile appearance catalog is missing."));
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static string CountryName(string code, string fallback, CultureInfo? language = null)
    {
        try { return new RegionInfo($"{(language ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName}-{code}").NativeName; }
        catch (ArgumentException) { return fallback; }
    }
}
