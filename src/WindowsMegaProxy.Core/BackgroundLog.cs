using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class BackgroundLog : IAsyncDisposable
{
    private sealed record Command(string? Message = null, int? Limit = null, TaskCompletionSource? Completion = null);
    private readonly DiagnosticLog log;
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(512);
    private readonly Task worker;
    private int dropped;
    public event Action<Exception>? Failed;

    public BackgroundLog(DiagnosticLog log) { this.log = log; worker = Task.Run(DrainAsync); }

    public void Write(string message)
    {
        if (!commands.Writer.TryWrite(new Command(Message: message.Length > 8192 ? message[..8192] : message)))
            Interlocked.Increment(ref dropped);
    }

    public Task SetLimitAsync(int megabytes) => ControlAsync(new Command(Limit: megabytes, Completion: new(TaskCreationOptions.RunContinuationsAsynchronously)));
    public Task FlushAsync() => ControlAsync(new Command(Completion: new(TaskCreationOptions.RunContinuationsAsynchronously)));

    private async Task ControlAsync(Command command)
    {
        await commands.Writer.WriteAsync(command).ConfigureAwait(false);
        await command.Completion!.Task.ConfigureAwait(false);
    }

    public async Task<string> ReadAsync(bool tailOnly = false)
    {
        await FlushAsync().ConfigureAwait(false);
        return await Task.Run(() => log.Read(tailOnly)).ConfigureAwait(false);
    }

    public async Task ExportAsync(string destination)
    {
        await FlushAsync().ConfigureAwait(false);
        await Task.Run(() => log.Export(destination)).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        var batch = new List<string>(16);
        void Flush()
        {
            int lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0) batch.Add($"Dropped {lost} log entries because the log queue was full.");
            try { log.WriteBatch(batch); }
            catch (Exception error) { Failed?.Invoke(error); }
            batch.Clear();
        }
        await foreach (var command in commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (command.Message is not null) batch.Add(command.Message);
            else
            {
                Flush();
                try
                {
                    if (command.Limit is int limit) log.SetLimit(limit);
                    command.Completion!.TrySetResult();
                }
                catch (Exception error) { command.Completion!.TrySetException(error); }
            }
            if (batch.Count >= 16 || !commands.Reader.TryPeek(out _)) Flush();
        }
        Flush();
    }

    public async ValueTask DisposeAsync()
    {
        commands.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }
}
