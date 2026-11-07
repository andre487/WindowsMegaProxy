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
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, AppIdentity.ServiceExecutable))
            { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--set-mode");
            start.ArgumentList.Add(StartupConfiguration.Validate(mode));
            using var helper = Process.Start(start) ?? throw new IOException("Could not start the mode configuration helper.");
            await QuitAsync();
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
