using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed class ServiceClient(string pipeName = ServiceProtocol.PipeName, string? expectedOwner = null)
{
    public bool CanEdit { get; private set; }
    public async Task<ServiceReply> CallAsync(ServiceRequest request, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var pipe = await ConnectAsync(deadline.Token).ConfigureAwait(false);
        await ServiceProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        var reply = await ServiceProtocol.ReadAsync<ServiceReply>(pipe, deadline.Token).ConfigureAwait(false);
        CanEdit = reply.CanEdit;
        if (reply.HostKey is not null) throw new ServiceHostKeyException(reply.HostKey);
        if (reply.Error is not null) throw new IOException(reply.Error);
        return reply;
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", pipeName,
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(3000, token).ConfigureAwait(false);
            var owner = (SecurityIdentifier)pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
            if (owner.Value != (expectedOwner ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value))
                throw new UnauthorizedAccessException("The service pipe has an untrusted owner.");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public async Task ExportLogAsync(string destination, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var pipe = await ConnectAsync(deadline.Token).ConfigureAwait(false);
            await ServiceProtocol.WriteAsync(pipe, new ServiceRequest("exportLog"), deadline.Token).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                long size = 0;
                while (true)
                {
                    var chunk = await ServiceProtocol.ReadAsync<ServiceLogChunk>(pipe, deadline.Token, () => 128 * 1024).ConfigureAwait(false);
                    if (chunk.Error is not null) throw new IOException(chunk.Error);
                    size += chunk.Data.Length;
                    if (chunk.Data.Length > 65536 || size > 100 * 1024 * 1024) throw new IOException("Invalid service log export.");
                    await output.WriteAsync(chunk.Data, deadline.Token).ConfigureAwait(false);
                    if (chunk.Done) break;
                }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class ServiceHostKeyException(SshHostKeyUpdate update) : IOException("The SSH server key changed.")
{
    public SshHostKeyUpdate Update { get; } = update;
}
