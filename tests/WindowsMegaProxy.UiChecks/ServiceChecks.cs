using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class ServiceChecks
{
    public static async Task RunAsync()
    {
        string name = AppIdentity.IsDev ? "WindowsMegaProxy.Dev" : "WindowsMegaProxy";
        Assert(ServiceProtocol.Name == name && ServiceProtocol.PipeName == name + ".Control.v3", "Build channels have separate services and versioned pipes.");
        Assert(Path.GetFileName(MachineSettingsStore.DefaultDirectory) == name && Path.GetFileName(SettingsStore.DirectoryPath) == name,
            "Build channels have separate machine and user settings.");
        Assert(AppIdentity.MutexName == @"Local\" + name, "Build channels do not share the UI instance mutex.");
        Assert(ClientAccess.MayEdit(true, false, false, false, false, false), "Administrator accounts may edit.");
        Assert(!ClientAccess.MayEdit(false, false, false, false, false, false), "Ordinary accounts are read-only.");
        Assert(!ClientAccess.MayEdit(true, false, true, false, false, false), "Restricted administrator tokens cannot edit.");
        Assert(!ClientAccess.MayEdit(true, false, false, true, false, false), "Low integrity cannot edit.");
        Assert(!ClientAccess.MayEdit(true, false, false, false, true, false), "App containers cannot edit.");
        Assert(!ClientAccess.MayEdit(true, false, false, false, false, true), "Network clients cannot edit.");
        using (var forged = new MemoryStream())
        {
            await ServiceProtocol.WriteAsync(forged, new { Operation = "save", CanEdit = true }, default);
            forged.Position = 0;
            bool invalid = false;
            try { await ServiceProtocol.ReadAsync<ServiceRequest>(forged, default); }
            catch (JsonException) { invalid = true; }
            Assert(invalid, "Clients cannot supply an authorization flag in the protocol.");
        }
        using (var oversized = new MemoryStream(BitConverter.GetBytes(ServiceProtocol.MaximumFrameBytes)))
        {
            bool invalid = false;
            try { await ServiceProtocol.ReadAsync<ServiceRequest>(oversized, default, () => 65536); }
            catch (IOException error) { invalid = error.Message == "Invalid service message length."; }
            Assert(invalid, "Read-only frame limits are checked before allocating or reading the body.");
        }
        var identity = ClientAccess.Current();
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string limitedName = "WindowsMegaProxy.LimitedAccess." + Guid.NewGuid().ToString("N");
        var limitedAcl = new PipeSecurity();
        limitedAcl.SetAccessRuleProtection(true, false);
        limitedAcl.SetOwner(new SecurityIdentifier(sid));
        limitedAcl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), ServicePipeServer.ClientRights, AccessControlType.Allow));
        using (var limited = NamedPipeServerStreamAcl.Create(limitedName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, limitedAcl))
        {
            var accepted = limited.WaitForConnectionAsync();
            using var clientPipe = new NamedPipeClientStream(".", limitedName, ServicePipeServer.ClientRights, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
            await clientPipe.ConnectAsync(3000);
            await accepted;
            Assert((ServicePipeServer.ClientRights & (PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions)) == 0,
                "A client can connect without administrator FullControl and cannot create server instances or change their ACL.");
        }
        Assert(identity.Sid == sid, "Windows token identity is captured.");
        string directory = Path.GetFullPath(Path.Combine("artifacts", "service-checks", Guid.NewGuid().ToString("N")));
        var store = new MachineSettingsStore(directory, machine: false);
        var config = new Settings { Language = "en" };
        var profile = ProxyProfile.Create("Shared profile");
        profile.Proxy["host"] = "proxy.invalid";
        profile.Proxy["password"] = "do-not-disclose-password";
        profile.Data["foreignSecret"] = "do-not-disclose-foreign-data";
        config.Profiles.Add(profile);
        config.ActiveProfileId = profile.Id;
        store.Save(config);
        Assert(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("do-not-disclose"), "Global configuration is encrypted at rest.");
        Assert(store.Load().ActiveProfile!.Proxy["password"]!.GetValue<string>() == "do-not-disclose-password", "Global DPAPI roundtrip.");
        var fake = new TestEngine();
        bool startup = false;
        await using var broker = new ServiceBroker(store, enabled => { startup = enabled; return Task.CompletedTask; }, () => fake);
        var admin = new ClientAccess(sid, true);
        var reader = new ClientAccess("readonly-fixture", false);
        foreach (string operation in new[] { "save", "start", "stop", "acceptHostKey", "http3", "connections", "log", "unknown" })
        {
            var denied = await broker.HandleAsync(reader, new(operation, Settings: config), default);
            Assert(denied.Error == "Read-only mode" && !fake.Running && !startup, "Read-only authorization: " + operation);
        }
        var visible = await broker.HandleAsync(reader, new("hello"), default);
        string serialized = JsonSerializer.Serialize(visible);
        PortableConfiguration.ValidateSettings(visible.Settings!);
        foreach (string type in new[] { "SSH", "HTTPS_JUMP", "SSH_JUMP" })
        {
            var typed = config.Copy();
            typed.ActiveProfile!.Proxy["type"] = type;
            if (type.EndsWith("_JUMP", StringComparison.Ordinal))
                typed.ActiveProfile.Proxy["jump"] = new System.Text.Json.Nodes.JsonObject { ["host"] = "jump.invalid", ["port"] = 443, ["password"] = "do-not-disclose-jump" };
            var filtered = ServiceView.PublicSettings(typed);
            PortableConfiguration.ValidateSettings(filtered);
            Assert(!JsonSerializer.Serialize(filtered).Contains("do-not-disclose"), "Filtered " + type + " profiles stay valid and contain no credentials.");
        }
        Assert(!serialized.Contains("do-not-disclose") && visible.Settings?.Profiles.Count == 1, "Read-only views preserve profile names but omit all secrets.");
        var started = await broker.HandleAsync(admin, new("start", InstanceId: broker.InstanceId), default);
        int starts = fake.Starts;
        var repeatedStart = await broker.HandleAsync(admin, new("start", InstanceId: broker.InstanceId), default);
        Assert(fake.Starts == starts && repeatedStart.Status!.Generation == started.Status!.Generation,
            "Repeated service start keeps the existing connection and its generation.");
        visible = await broker.HandleAsync(reader, new("status"), default);
        Assert(visible.Status?.Running == true && visible.Status.AppliedSignature is null && visible.Probe is null,
            "Read-only status cannot leak configuration signatures or diagnostic credentials.");
        foreach (var (operation, lateResult) in new[] { ("http3", false), ("connections", false), ("http3", true) })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Delay(CancellationToken token)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (lateResult) { }
            }
            fake.Http3 = async token => { await Delay(token); return "old result"; };
            fake.Connections = operation == "connections" ? async token => { await Delay(token); return new([], 0, 0, 0); } : null;
            var reading = broker.HandleAsync(admin, new(operation, InstanceId: broker.InstanceId, Generation: started.Status!.Generation), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (operation == "http3")
                await broker.HandleAsync(admin, new("connections", InstanceId: broker.InstanceId, Generation: started.Status.Generation), default).WaitAsync(TimeSpan.FromSeconds(5));
            await broker.HandleAsync(admin, new("stop", InstanceId: broker.InstanceId, Generation: started.Status.Generation), default).WaitAsync(TimeSpan.FromSeconds(5));
            bool cancelled = false;
            try { await reading; }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !fake.Running, "Stopping cancels an in-flight " + operation + " request without waiting for its network timeout.");
            fake.Http3 = null; fake.Connections = null;
            started = await broker.HandleAsync(admin, new("start", InstanceId: broker.InstanceId), default);
            var fresh = await broker.HandleAsync(admin, new("connections", InstanceId: broker.InstanceId, Generation: started.Status!.Generation), default);
            Assert(fresh.Connections is not null, "Core reads work again after reconnecting.");
        }
        await broker.HandleAsync(admin, new("stop", InstanceId: broker.InstanceId, Generation: started.Status!.Generation), default);
        var test = await broker.HandleAsync(admin, new("start", TestOnly: true, InstanceId: broker.InstanceId), default);
        Assert(test.Lease is not null, "Temporary diagnostics receive a lease.");
        bool deniedLease = false;
        try { await broker.HandleAsync(admin, new("stop", Lease: "wrong", InstanceId: broker.InstanceId, Generation: test.Status!.Generation), default); }
        catch (ServiceStateChangedException) { deniedLease = true; }
        Assert(deniedLease && fake.Running, "A wrong lease cannot stop another test.");
        await broker.HandleAsync(admin, new("stop", Lease: test.Lease, InstanceId: broker.InstanceId, Generation: test.Status!.Generation), default);
        var updated = config.Copy(); updated.Theme = "dark";
        var saved = await broker.HandleAsync(admin, new("save", Settings: updated, Revision: 0, InstanceId: broker.InstanceId), default);
        Assert(saved.Revision == 1 && saved.Settings!.Theme == "dark", "Global settings have a shared revision.");
        bool stale = false;
        try { await broker.HandleAsync(admin, new("save", Settings: config, Revision: 0, InstanceId: broker.InstanceId), default); }
        catch (IOException) { stale = true; }
        Assert(stale && store.Load().Theme == "dark", "A stale client cannot overwrite another administrator's settings.");

        string pipeName = "WindowsMegaProxy.Test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = new ServicePipeServer(broker, pipeName, machine: false, _ => reader);
        Task serving = server.RunAsync(stop.Token);
        try
        {
            var client = new ServiceClient(pipeName, sid);
            var reply = await client.CallAsync(new("hello"));
            Assert(!reply.CanEdit && reply.Settings?.ActiveProfile?.Name == "Shared profile", "Framed named-pipe request roundtrip.");
            bool refused = false;
            try { await client.CallAsync(new("save", Settings: config)); }
            catch (IOException error) { refused = error.Message == "Read-only mode"; }
            Assert(refused && !client.CanEdit, "The server enforces authorization over IPC.");
            string export = Path.Combine(directory, "readonly-export.log");
            await File.WriteAllTextAsync(export, "existing export");
            refused = false;
            try { await client.ExportLogAsync(export); }
            catch (IOException error) { refused = error.Message == "Read-only mode"; }
            Assert(refused && await File.ReadAllTextAsync(export) == "existing export", "Denied log export preserves the destination.");
            refused = false;
            try { await new ServiceClient(pipeName, "S-1-5-18").CallAsync(new("hello")); }
            catch (UnauthorizedAccessException) { refused = true; }
            Assert(refused, "A spoofed service pipe is rejected before sending configuration.");
        }
        finally { stop.Cancel(); await serving; }
        pipeName = "WindowsMegaProxy.Test." + Guid.NewGuid().ToString("N");
        using var stopAdmin = new CancellationTokenSource();
        serving = new ServicePipeServer(broker, pipeName, machine: false, _ => admin).RunAsync(stopAdmin.Token);
        try
        {
            var client = new ServiceClient(pipeName, sid);
            var remote = new RemoteEngine(client, _ => Task.FromResult(false));
            await remote.StartAsync(config);
            var applied = remote.AppliedSettings;
            Assert(applied is not null, "Start returns the applied configuration once.");
            var stats = await client.CallAsync(new("connections", InstanceId: remote.InstanceId, Generation: remote.Generation));
            Assert(stats.Status?.AppliedSettings is null && stats.Settings is null, "Statistics do not resend profiles and rules.");
            remote.Apply(stats);
            Assert(ReferenceEquals(remote.AppliedSettings, applied), "Lightweight replies retain the client's applied snapshot.");
            var logReply = await client.CallAsync(new("log"));
            Assert(logReply.Status?.AppliedSettings is null, "Log polling does not resend the applied snapshot.");
            var rebound = await client.CallAsync(new("hello"));
            Assert(rebound.Status?.AppliedSettings is not null, "A new client binding still receives the full applied snapshot.");
            var controller = new ConnectionController(() => remote);
            controller.Attach(remote);
            await controller.ShutdownAsync();
            Assert(fake.Running, "Closing a service UI must leave the shared connection running.");
            string export = Path.Combine(directory, "service-export.log");
            await client.ExportLogAsync(export);
            Assert((await File.ReadAllTextAsync(export)).Contains("Service TUN connected."), "The complete service log is exported through IPC.");
            await remote.StopAsync();
        }
        finally { stopAdmin.Cancel(); await serving; }
        pipeName = "WindowsMegaProxy.Test." + Guid.NewGuid().ToString("N");
        using var stopIdentity = new CancellationTokenSource();
        serving = new ServicePipeServer(broker, pipeName, machine: false).RunAsync(stopIdentity.Token);
        try
        {
            var actual = await new ServiceClient(pipeName, sid).CallAsync(new("hello"));
            Assert(actual.CanEdit == identity.CanEdit, "Named-pipe impersonation authenticates the real Windows token.");
        }
        finally { stopIdentity.Cancel(); await serving; }
        Console.WriteLine("Service authorization, encrypted settings, diagnostic leases and named pipes passed (no service installation or TUN).");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
