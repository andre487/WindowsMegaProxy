using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private async void OperatingModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingUi || !IsLoaded || quitting || !canEdit || OperatingModeBox.SelectedValue is not string mode || mode == StartupConfiguration.Mode) return;
        try
        {
            OperatingModeBox.IsEnabled = false;
            await SaveSettingsAsync();
            await FlushSettingsAsync();
            var start = StartupConfiguration.ModeCommand(AppContext.BaseDirectory, installScript, mode);
            if (!File.Exists(start.FileName)) throw new IOException(AppIdentity.IsDev
                ? "The mode configuration helper is missing. Run setup.cmd for DEV."
                : "The mode configuration helper is missing. Repair the installation using the installer.");
            using var helper = Process.Start(start) ?? throw new IOException("Could not start the mode configuration helper.");
            // The helper closes this window through the graceful restart command after it starts.
            await helper.WaitForExitAsync();
            if (!quitting)
            {
                if (helper.ExitCode != 0) throw new IOException("Could not change the operating mode. See the application log or repair the installation using the installer.");
                updatingUi = true;
                OperatingModeBox.SelectedValue = StartupConfiguration.Mode;
                updatingUi = false;
                SetBusy(busy);
            }
        }
        catch (Exception error)
        {
            updatingUi = true;
            OperatingModeBox.SelectedValue = StartupConfiguration.Mode;
            updatingUi = false;
            ShowError(error);
            SetBusy(busy);
        }
    }
}
