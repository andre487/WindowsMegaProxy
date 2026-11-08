using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private async Task<bool> PrepareHttp3CoreAsync(Settings requested)
    {
        if (!Configuration.RequiresHttp3(requested)) return true;
        if (!requested.Http3Enabled) throw new InvalidOperationException("Enable HTTP/3 in settings before using this profile.");
        preparingCore = true;
        SetBusy(true);
        try
        {
            bool installed = await Task.Run(() =>
            {
                if (AppIdentity.IsDev)
                {
                    string dev = Environment.GetEnvironmentVariable("WINDOWSMEGAPROXY_H3_CORE") ??
                        Path.Combine(AppContext.BaseDirectory, "dependencies", "sing-box-h3", AppIdentity.CoreExecutable);
                    if (File.Exists(dev)) return true;
                }
                try { _ = CoreInstallation.Resolve(http3: true); return true; }
                catch (FileNotFoundException) { return false; }
            });
            if (installed) return true;
            if (MessageBox.Show(this,
                Ui.F("HTTP/3 requires downloading sing-box {0}. The archive will be verified against its pinned SHA-256. Download and install it now?", Configuration.Http3CoreVersion),
                "HTTP/3", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
            string helper = Path.Combine(AppContext.BaseDirectory, "WindowsMegaProxy.Http3Core.exe");
            if (!File.Exists(helper)) throw new FileNotFoundException("The HTTP/3 installer is missing. Repair the application installation.");
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = true,
                Verb = StartupConfiguration.IsElevated ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the HTTP/3 core installer.");
            await TrackFileAsync(process.WaitForExitAsync());
            if (process.ExitCode != 0) throw new IOException("HTTP/3 core installation failed. See the setup log and retry.");
            await Task.Run(() => CoreInstallation.Resolve(http3: true));
            WriteLog("Installed the checksum-verified HTTP/3 core " + Configuration.Http3CoreVersion + ".");
            return true;
        }
        finally { preparingCore = false; SetBusy(busy); }
    }
}
