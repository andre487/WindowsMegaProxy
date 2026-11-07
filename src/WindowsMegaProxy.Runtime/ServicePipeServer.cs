using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class ServicePipeServer(ServiceBroker broker, string pipeName = ServiceProtocol.PipeName,
    bool machine = true, Func<NamedPipeServerStream, ClientAccess>? authenticate = null)
{
    public async Task RunAsync(CancellationToken token)
    {
        using var slots = new SemaphoreSlim(16);
        var clients = new HashSet<Task>();
        bool first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = OpenPipe(first);
                    first = false;
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var handler = HandleAndReleaseAsync(pipe, token, slots);
                    pipe = null;
                    clients.RemoveWhere(task => task.IsCompleted);
                    clients.Add(handler);
                }
                catch { pipe?.Dispose(); slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients).ConfigureAwait(false); }
    }

    private async Task HandleAndReleaseAsync(NamedPipeServerStream pipe, CancellationToken token, SemaphoreSlim slots)
    {
        try { await HandleAsync(pipe, token).ConfigureAwait(false); }
        finally { slots.Release(); }
    }

    private NamedPipeServerStream OpenPipe(bool first)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(machine ? system : WindowsIdentity.GetCurrent().User!);
        acl.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!machine) acl.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // GenericWrite includes CreateNewInstance for pipes; grant only the individual client rights.
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 4096, 4096, acl);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            ClientAccess? peer = null;
            try
            {
                var request = await ServiceProtocol.ReadAsync<ServiceRequest>(pipe, deadline.Token, () =>
                {
                    peer = authenticate is null ? ClientAccess.Capture(pipe) : authenticate(pipe);
                    return peer.CanEdit ? ServiceProtocol.MaximumFrameBytes : 64 * 1024;
                }).ConfigureAwait(false);
                deadline.CancelAfter(TimeSpan.FromSeconds(110));
                if (request.Operation == "exportLog")
                {
                    if (!peer!.CanEdit)
                        await ServiceProtocol.WriteAsync(pipe, new ServiceLogChunk([], Error: "Read-only mode"), deadline.Token).ConfigureAwait(false);
                    else await broker.ExportLogAsync(peer, pipe, deadline.Token).ConfigureAwait(false);
                    return;
                }
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                Task disconnected = WatchDisconnectAsync(pipe, deadline, watch.Token);
                ServiceReply reply;
                try
                {
                    try { reply = await broker.HandleAsync(peer!, request, deadline.Token).ConfigureAwait(false); }
                    catch (ServiceHostKeyException key) { reply = new(peer!.CanEdit, HostKey: key.Update); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { reply = new(peer!.CanEdit, Error: peer.CanEdit ? DiagnosticLog.Redact(error.Message) : "Read-only mode"); }
                }
                finally { watch.Cancel(); await disconnected.ConfigureAwait(false); }
                await ServiceProtocol.WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource request, CancellationToken token)
    {
        try { await pipe.ReadAsync(new byte[1], token).ConfigureAwait(false); request.Cancel(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { request.Cancel(); }
    }
}
