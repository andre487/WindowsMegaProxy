using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WindowsMegaProxy;

public sealed class ProxyProfile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountryName)));
    public JsonObject Data { get; set; } = new();

    public ProxyProfile() { }
    public ProxyProfile(JsonObject data) => Data = data;

    [JsonIgnore] public string Id => Data["id"]!.GetValue<string>();
    [JsonIgnore] public string Name => Data["name"]?.GetValue<string>() is { Length: > 0 } name ? name : Host;
    [JsonIgnore] public JsonObject Proxy => Data["proxy"]!.AsObject();
    [JsonIgnore] public string Type => Proxy["type"]!.GetValue<string>();
    [JsonIgnore] public string Host => Proxy["host"]!.GetValue<string>();
    [JsonIgnore] public int Port => Proxy["port"]!.GetValue<int>();
    [JsonIgnore] public bool Supported => Type is "HTTPS" or "HTTPS_JUMP" or "SOCKS5" or "HTTP" or "MASQUE" or "SSH";
    [JsonIgnore] public bool PreferHttp3 => Type is "HTTPS" or "HTTPS_JUMP" && (Proxy["preferHttp3"]?.GetValue<bool>() ?? false);
    [JsonIgnore] public bool UsesHttp3 => Type == "MASQUE" || PreferHttp3;
    [JsonIgnore] public string Description => $"{Host}:{Port} · {Type}";
    [JsonIgnore] public string SelectionLabel => $"{Name} · {Type}";
    [JsonIgnore] public string Availability => Supported ? "" : Ui.T("This connection type is not supported by the Windows client yet");
    [JsonIgnore] public int ColorIndex => (Data["color"]?.GetValue<int>() ?? 0) % ProfileAppearance.ColorCount;
    [JsonIgnore] public string ColorHex => ProfileAppearance.Colors[ColorIndex].Hex;
    [JsonIgnore] public string CountryCode => Data["countryCode"]?.GetValue<string>() ?? "";
    [JsonIgnore] public string CountryName => ProfileAppearance.Country(CountryCode).Label;
    [JsonIgnore] public string? FlagUri => ProfileAppearance.Country(CountryCode).FlagUri;

    public ProxyProfile Copy() => new((JsonObject)Data.DeepClone());
    public override string ToString() => Name;

    public static ProxyProfile Create(string? name = null) => new(new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString(), ["name"] = name ?? Ui.T("New profile"),
        ["proxy"] = new JsonObject { ["type"] = "HTTPS", ["host"] = "", ["port"] = 443 }
    });
}
