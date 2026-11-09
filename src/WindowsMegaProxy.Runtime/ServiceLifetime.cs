using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public static class ServiceLifetime
{
    public static async Task WatchAsync(Task serving, Task maintenance, CancellationToken stopping)
    {
        await await Task.WhenAny(serving, maintenance).ConfigureAwait(false);
        if (!stopping.IsCancellationRequested) throw new IOException("A required service worker stopped unexpectedly.");
    }

    public static void ExitAfterFailure(Action report)
    {
        // SCM must see an abnormal exit even when Windows Event Log is unavailable.
        try { report(); }
        finally { Environment.Exit(1); }
    }
}
