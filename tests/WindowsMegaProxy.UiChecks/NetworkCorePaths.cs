using System;
using System.IO;

internal static class NetworkCorePaths
{
    public static string Client(bool experimental = false) => Path.GetFullPath(
        Environment.GetEnvironmentVariable("MEGAPROXY_TEST_NATIVE") == "1"
            ? $".tools/dev-cores/{(experimental ? "sing-box-h3" : "sing-box")}/WindowsMegaProxy.Dev.Core.exe"
            : $".tools/{(experimental ? "sing-box-h3" : "sing-box")}/sing-box.exe");
}
