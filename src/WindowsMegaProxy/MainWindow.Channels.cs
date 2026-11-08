using System.Threading.Tasks;

namespace WindowsMegaProxy;

public partial class MainWindow
{
    private async Task<bool> ResolveChannelAsync()
    {
        if (!automaticUpdateChecks) return true;
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
