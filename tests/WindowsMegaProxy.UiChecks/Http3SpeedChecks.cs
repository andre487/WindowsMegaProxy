using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class Http3SpeedChecks
{
    public static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancel = new CancellationTokenSource();
        var measurement = Http3Speed.MeasureAsync(new(port, "synthetic-secret"), "Through profile", 10, _ => { }, _ => { }, cancel.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        byte[] greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting, deadline.Token);
        if (greeting[0] != 5 || greeting[2] != 2) throw new Exception("HTTP/3 must use authenticated SOCKS5 rather than a direct route.");
        cancel.Cancel();
        try { await measurement.WaitAsync(deadline.Token); throw new Exception("Cancelled HTTP/3 measurement completed successfully."); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !deadline.IsCancellationRequested) { }
        byte[] remaining = new byte[1];
        try
        {
            if (await stream.ReadAsync(remaining, deadline.Token) != 0) throw new Exception("HTTP/3 helper remained connected after cancellation.");
        }
        catch (System.IO.IOException) { }
        Console.WriteLine("HTTP/3 diagnostic client uses the authenticated relay and closes it on cancellation; no external traffic.");
    }
}
