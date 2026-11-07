using System.Reflection;

namespace WindowsMegaProxy;

internal static class BuildInformation
{
    public static string Display { get; } = Format(typeof(BuildInformation).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    internal static string Format(string information)
    {
        string[] parts = information.Split('+', 2);
        string commit = parts.Length == 2 ? parts[1] : "unknown";
        return $"v{parts[0]} · {commit[..System.Math.Min(7, commit.Length)]}";
    }
}
