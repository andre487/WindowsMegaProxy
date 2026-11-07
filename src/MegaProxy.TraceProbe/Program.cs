using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MegaProxy.TraceProbe;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"Executable: {Environment.ProcessPath}");
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        _ = Task.Run(() =>
        {
            try
            {
                if (Console.IsInputRedirected) Console.ReadLine();
                else while (Console.ReadKey(intercept: true).Key != ConsoleKey.Enter) { }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            stop.Cancel();
        });
        try
        {
            // Test the application's TUN route independently of Windows system-proxy settings.
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
            return await ProbeRunner.RunAsync(client, Console.Out, stop.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
