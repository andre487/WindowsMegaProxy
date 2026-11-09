using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private bool channelConflictPending;

    private bool NotifyChannelConflictIfHidden(bool otherRunning)
    {
        if (!otherRunning || IsVisible) return false;
        channelConflictPending = true;
        updateNoticeVersion = null;
        WriteLog("Another MegaProxy channel is running; waiting for the user's choice in the tray client.");
        tray.BalloonTipTitle = Ui.T("Another MegaProxy version is running");
        tray.BalloonTipText = Ui.F("{0} is running. Click to choose which version to keep.", ChannelControl.OtherDisplayName);
        tray.BalloonTipIcon = Forms.ToolTipIcon.Warning;
        tray.ShowBalloonTip(10000);
        return true;
    }

    private async Task OpenPendingChannelConflictAsync()
    {
        if (!channelConflictPending) return;
        channelConflictPending = false;
        try
        {
            if (await ResolveChannelAsync() && canEdit && settings.AutoConnect && !busy && engine?.Running != true)
                await ToggleAsync();
        }
        catch (System.Exception error) { ShowError(error); }
    }

    private async Task<bool> ResolveChannelAsync()
    {
        if (!automaticUpdateChecks) return true;
        if (!IsVisible && NotifyChannelConflictIfHidden(await Task.Run(ChannelControl.OtherRunning))) return false;
        IsEnabled = false;
        try
        {
            await FlushSettingsFormAsync();
            await FlushSettingsAsync();
            if (await ChannelConflict.ResolveAsync(this)) return true;
            await QuitAsync();
            return false;
        }
        finally { if (!quitting) IsEnabled = true; }
    }
}
