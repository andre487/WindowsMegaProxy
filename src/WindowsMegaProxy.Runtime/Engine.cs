using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed partial class Engine : IEngine
{
    private Process? process;
    private HttpClient client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private string configPath = "";
    private readonly string stateDirectory;
    private readonly Uri http3ProbeUri;
    private readonly bool allowEnvironmentOverrides;
    private readonly Func<bool> otherRunning;
    private ProcessJob? job;
    private IDisposable? consoleLease;
    public bool Running => process is { HasExited: false };
    public event Action? Exited;
    public event Action<string>? Log;
    public Settings? AppliedSettings { get; private set; }
    public string? AppliedSignature { get; private set; }
    public bool Http3Selected { get; private set; }
    public bool UsedHttp3Fallback { get; private set; }
    public Func<SshHostKeyUpdate, Task<bool>>? ConfirmSshHostKeyChange { get; set; }
    public Func<SshHostKeyUpdate, Task>? SaveSshHostKey { get; set; }
    private string[] outboundTags = [];
    private int probePort;
    private string probeSecret = "";
    private Task drain = Task.CompletedTask;
    private CancellationTokenSource? listStopping;
    private Task listUpdates = Task.CompletedTask;
    private string[] podkopWarnings = [];
    public string SelectedOutbound
    {
        get
        {
            string active = "profile:" + AppliedSettings?.ActiveProfileId;
            return outboundTags.Contains("proxy") ? "proxy" : outboundTags.Contains(active) ? active : outboundTags.FirstOrDefault() ?? "direct";
        }
    }
    public bool HasProbeProfile => SelectedOutbound != "direct";

    public Engine(string? stateDirectory = null, Uri? http3ProbeUri = null, bool allowEnvironmentOverrides = true, Func<bool>? otherRunning = null)
    {
        this.allowEnvironmentOverrides = allowEnvironmentOverrides;
        this.otherRunning = otherRunning ?? ChannelControl.OtherRunning;
        this.stateDirectory = stateDirectory ?? SettingsStore.DirectoryPath;
        this.http3ProbeUri = http3ProbeUri ?? new Uri("https://example.com/");
    }

    private async Task StartSelectedAsync(Settings settings, bool testOnly, CancellationToken cancellationToken, bool? validatedHttp3 = null)
    {
        if (Running) return;
        if (!testOnly && otherRunning())
            throw new IOException("The other MegaProxy version is running. Open the application and choose which version to keep active.");
        UsedHttp3Fallback = validatedHttp3 == false;
        var runtime = PortableConfiguration.Copy(settings);
        if (UsedHttp3Fallback)
        {
            runtime.ActiveProfile!.Proxy["preferHttp2"] = true;
            runtime.ActiveProfile.Proxy["preferHttp3"] = false;
        }
        if (validatedHttp3.HasValue) Log?.Invoke("Reusing the failover candidate's verified HTTP transport.");
        if (Configuration.UsesProxy(runtime) && runtime.ActiveProfile is { Type: "SSH" } ssh && !(ssh.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false))
        {
            string previous = ssh.Proxy["trustedHostKey"]?.GetValue<string>() ?? "";
            string key = await SshConfiguration.ReadHostKeyAsync(ssh.Host, ssh.Port, previous, cancellationToken, allowChangedKey: true);
            string fingerprint = SshConfiguration.Fingerprint(key);
            var update = new SshHostKeyUpdate(ssh.Id, ssh.Name, ssh.Host, ssh.Port, previous, fingerprint, key);
            if (previous.Length > 0 && previous != fingerprint && (ConfirmSshHostKeyChange is null || !await ConfirmSshHostKeyChange(update)))
                throw new InvalidOperationException("The SSH server key changed. Connection cancelled.");
            if ((previous != fingerprint || SshConfiguration.PublicKey(ssh) != key) && SaveSshHostKey is not null) await SaveSshHostKey(update).ConfigureAwait(false);
            if (ssh.Data["windows"] is not JsonObject) ssh.Data["windows"] = new JsonObject();
            ssh.Data["windows"]!["sshHostKey"] = key;
            ssh.Proxy["trustedHostKey"] = fingerprint;
            Log?.Invoke(previous.Length == 0 ? "Accepted and pinned a new SSH server key." : "Verified the SSH server key.");
        }
        if (validatedHttp3 is null && Configuration.RequiresHttp3(settings) && settings.ActiveProfile!.PreferHttp3)
        {
            var config = Configuration.Build(runtime, 1, "probe");
            config["inbounds"] = new JsonArray();
            config["route"]!["rules"] = new JsonArray();
            config["route"]!.AsObject().Remove("rule_set");
            config["route"]!["final"] = "proxy";
            config.Remove("experimental");
            config["log"] = new JsonObject { ["disabled"] = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            Log?.Invoke("Checking HTTP/3 proxy availability without TUN.");
            (int ExitCode, string Error) result;
            try { result = await RunCoreToolAsync(CoreExecutable(alpha: true), config, http3ProbeUri.ToString(), deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("The HTTP/3 availability check timed out."); }
            if (result.ExitCode != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Http3Fallback.IsUnavailable([result.Error]))
                {
                    Log?.Invoke("HTTP/3 selection failed; refusing automatic TCP fallback.");
                    throw new HttpRequestException("HTTP/3 proxy connection failed. Check the certificate, credentials and server compatibility.");
                }
                runtime.ActiveProfile!.Proxy["preferHttp2"] = true;
                runtime.ActiveProfile.Proxy["preferHttp3"] = false;
                UsedHttp3Fallback = true;
                Log?.Invoke("HTTP/3 unavailable; selecting HTTP/2 with HTTP/1.1 fallback on the alpha core.");
            }
            else Log?.Invoke("HTTP/3 selected; the proxy test request succeeded.");
        }
        await StartCoreAsync(runtime, testOnly, cancellationToken);
        AppliedSettings = PortableConfiguration.Copy(settings);
        SetPodkopWarningMetadata(AppliedSettings, podkopWarnings);
        if (runtime.ActiveProfile is { Type: "SSH" } appliedSsh && AppliedSettings.ActiveProfile is { } savedSsh)
        {
            if (savedSsh.Data["windows"] is not JsonObject) savedSsh.Data["windows"] = new JsonObject();
            savedSsh.Data["windows"]!["sshHostKey"] = appliedSsh.Data["windows"]?["sshHostKey"]?.DeepClone();
            savedSsh.Proxy["trustedHostKey"] = appliedSsh.Proxy["trustedHostKey"]?.DeepClone();
        }
        AppliedSignature = Configuration.Signature(AppliedSettings);
        if (!testOnly && PodkopCatalog.Used(runtime).Length > 0)
        {
            listStopping = new CancellationTokenSource();
            var listToken = listStopping.Token;
            listUpdates = Task.Run(() => UpdatePodkopAsync(runtime, listToken), listToken);
        }
    }

    private string CoreExecutable(bool alpha) => (allowEnvironmentOverrides ? Environment.GetEnvironmentVariable(alpha ? "WINDOWSMEGAPROXY_H3_CORE" : "WINDOWSMEGAPROXY_CORE") : null)
        ?? Path.Combine(AppContext.BaseDirectory, "dependencies", alpha ? "sing-box-h3" : "sing-box", AppIdentity.CoreExecutable);

    private async Task StartCoreAsync(Settings settings, bool testOnly, CancellationToken cancellationToken)
    {
        if (Running) return;
        Http3Selected = Configuration.RequiresHttp3(settings);
        bool alpha = Configuration.RequiresAlpha(settings);
        var executable = CoreExecutable(alpha);
        if (!File.Exists(executable)) throw new FileNotFoundException(AppIdentity.IsDev
            ? "The development core is missing. Run setup.cmd and run.cmd."
            : "The bundled network core is missing. Repair the application using the installer.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var config = Configuration.Build(settings, port, secret);
        if (!testOnly)
        {
            var addresses = await Task.Run(() =>
            {
                var networks = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => !string.Equals(adapter.Name, AppIdentity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Select(address => TunAddresses.Network(address.Address, address.PrefixLength));
                return TunAddresses.Select(networks, AppIdentity.IsDev);
            }, cancellationToken).ConfigureAwait(false);
            config["inbounds"]![0]!["address"] = new JsonArray(addresses.Ipv4, addresses.Ipv6);
            Log?.Invoke($"TUN interface {AppIdentity.DisplayName}: IPv4={addresses.Ipv4}; IPv6={addresses.Ipv6}.");
        }
        var cache = new PodkopCache(Path.Combine(stateDirectory, "podkop"), message => Log?.Invoke(message));
        foreach (string id in PodkopCatalog.Used(settings)) await cache.GetAsync(id, cancellationToken,
            cacheOnly: PodkopThroughProxy(settings, id), preferCache: true).ConfigureAwait(false);
        foreach (var ruleSet in config["route"]!["rule_set"]!.AsArray())
            ruleSet!["path"] = cache.MergeGroup(ruleSet["tag"]!.ToString()[7..].Split('+'));
        podkopWarnings = cache.Unavailable.ToArray();
        AppliedSettings = PortableConfiguration.Copy(settings);
        AppliedSignature = Configuration.Signature(settings);
        outboundTags = config["outbounds"]!.AsArray().Select(item => item!["tag"]!.ToString()).Where(tag => tag != "direct").ToArray();
        if (testOnly) config["inbounds"] = new JsonArray();
        using (var probeListener = new TcpListener(IPAddress.Loopback, 0))
        {
            probeListener.Start();
            probePort = ((IPEndPoint)probeListener.LocalEndpoint).Port;
        }
        listener.Stop();
        probeSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        config["inbounds"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "socks", ["tag"] = "diagnostic-proxy", ["listen"] = "127.0.0.1", ["listen_port"] = probePort,
            ["users"] = new JsonArray(new JsonObject { ["username"] = "diagnostic", ["password"] = probeSecret })
        });
        config["route"]!["rules"]!.AsArray().Insert(0, new JsonObject
        {
            ["inbound"] = new JsonArray("diagnostic-proxy"), ["action"] = "route", ["outbound"] = SelectedOutbound
        });
        Log?.Invoke($"Starting {(alpha ? Configuration.Http3CoreVersion : Configuration.CoreVersion)} core; HTTP/3={Http3Selected}; HTTP/2={settings.ActiveProfile?.PreferHttp2 == true}; TUN={!testOnly}.");
        client.Dispose();
        client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        configPath = Path.Combine(stateDirectory, testOnly ? $"test-runtime-{Guid.NewGuid():N}.json" : "runtime.json");
        try
        {
            await File.WriteAllTextAsync(configPath, config.ToJsonString(Configuration.JsonOptions));
            var check = await CoreProcess.RunAsync(StartInfo(executable, "check"), TimeSpan.FromSeconds(30), cancellationToken);
            if (check.ExitCode != 0)
            {
                Log?.Invoke(check.Error);
                throw new InvalidOperationException("sing-box rejected the configuration. Check the profile and rules.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            consoleLease = await CoreConsole.AcquireAsync(cancellationToken);
            job = new ProcessJob();
            process = Process.Start(StartInfo(executable, "run")) ?? throw new InvalidOperationException("The core did not start.");
            job.Assign(process);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { if (!changingProfile) Exited?.Invoke(); };
            drain = Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (!Running) throw new InvalidOperationException("The core exited during startup. TUN may already be in use.");
                try
                {
                    using var response = await client.GetAsync("version", cancellationToken);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(150, cancellationToken);
            }
            throw new TimeoutException("The core did not become ready.");
        }
        catch { await StopSelectedAsync(); throw; }
    }

    public async Task<ConnectionSnapshot> GetConnectionsAsync(bool showAll)
    {
        using var response = await client.GetAsync("connections").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) ?? throw new IOException("Empty core response.");
        return ConnectionSnapshot.Parse(root, AppliedSettings!, showAll);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken token)
    {
        if (!Running) return false;
        try
        {
            using var response = await client.GetAsync("version", token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public ProbeAccess ProbeAccess => new(probePort, probeSecret);

    public HttpClient CreateProbeClient(bool throughProfile)
    {
        if (!Running) throw new InvalidOperationException("Network core stopped.");
        if (AppliedSettings?.DefaultAction == "block" && !HasProbeProfile) throw new InvalidOperationException("Block mode has no profile available for testing.");
        if (throughProfile && !HasProbeProfile) throw new InvalidOperationException("No applied profile is available for testing.");
        var handler = new HttpClientHandler { UseProxy = throughProfile, AllowAutoRedirect = false, MaxResponseHeadersLength = 64 };
        if (throughProfile) handler.Proxy = new WebProxy($"socks5://127.0.0.1:{probePort}") { Credentials = new NetworkCredential("diagnostic", probeSecret) };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
    }

    public async Task<string> CheckHttp3Async(CancellationToken token)
    {
        if (!Running) throw new InvalidOperationException("Network core stopped.");
        bool supported = Http3Selected;
        if (AppliedSettings!.ActiveProfile is { Type: "SOCKS5" } profile && HasProbeProfile)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var association = await SocksUdpAssociation.OpenAsync(profile.Host, profile.Port,
                    profile.Proxy["username"]?.GetValue<string>() ?? "", profile.Proxy["password"]?.GetValue<string>() ?? "", timeout.Token);
                supported = association is not null;
            }
            catch (Exception error) when (error is IOException or SocketException || error is OperationCanceledException && !token.IsCancellationRequested)
            { Log?.Invoke("SOCKS5 UDP capability could not be confirmed; skipping HTTP/3 diagnostics."); }
        }
        if (!supported) return "";
        var results = new System.Collections.Generic.List<string>();
        foreach (string host in new[] { "www.cloudflare.com", "quic.browserleaks.com" })
        {
            bool confirmed = await FetchHttp3Async(host, token);
            results.Add($"HTTP/3 · {host}: {(confirmed ? "confirmed" : "unavailable")}");
        }
        return string.Join(Environment.NewLine, results);
    }

    private async Task<bool> FetchHttp3Async(string host, CancellationToken token)
    {
        // The stable core's fetch command supports HTTP/3 over the authenticated SOCKS5 UDP relay.
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["disabled"] = true },
            ["outbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks", ["tag"] = "probe", ["server"] = "127.0.0.1", ["server_port"] = probePort,
                ["version"] = "5", ["username"] = "diagnostic", ["password"] = probeSecret
            }),
            ["route"] = new JsonObject { ["final"] = "probe" }
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        string url = $"http3://{host}" + (host == "www.cloudflare.com" ? "/cdn-cgi/trace" : "/");
        try { return (await RunCoreToolAsync(CoreExecutable(alpha: false), config, url, deadline.Token)).ExitCode == 0; }
        catch (Exception error) when (error is IOException || error is OperationCanceledException && !token.IsCancellationRequested) { return false; }
    }

    private async Task<(int ExitCode, string Error)> RunCoreToolAsync(string executable, JsonObject config, string url, CancellationToken token)
    {
        string path = Path.Combine(stateDirectory, $"http3-probe-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, config.ToJsonString(), token);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (string argument in new[] { "-c", path, "--disable-color", "tools", "fetch", url }) start.ArgumentList.Add(argument);
            var result = await CoreProcess.RunAsync(start, TimeSpan.FromSeconds(15), token);
            return (result.ExitCode, result.Error);
        }
        finally { File.Delete(path); }
    }

    private async Task StopSelectedAsync()
    {
        listStopping?.Cancel();
        try { await listUpdates.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log?.Invoke("Podkop worker failed: " + error.GetType().Name + "; " + error.Message); }
        listStopping?.Dispose(); listStopping = null; listUpdates = Task.CompletedTask;
        try
        {
            if (Running)
            {
                CoreConsole.Stop();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process!.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { job?.Dispose(); job = null; }
            }
        }
        finally
        {
            job?.Dispose(); job = null;
            try { await drain.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                process?.Dispose(); process = null;
                consoleLease?.Dispose(); consoleLease = null;
                if (configPath.Length > 0) File.Delete(configPath);
            }
        }
        Log?.Invoke("Network core stopped.");
    }

    private static bool PodkopThroughProxy(Settings settings, string id) => settings.Rules.Any(rule => rule.Enabled && rule.PodkopThroughProxy && PodkopCatalog.Parse(rule.Destination.Trim()).Contains(id));

    private async Task UpdatePodkopAsync(Settings settings, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        var cache = new PodkopCache(Path.Combine(stateDirectory, "podkop"), message => Log?.Invoke(message));
        do
        {
            foreach (string id in PodkopCatalog.Used(settings))
                try
                {
                    using var transport = PodkopThroughProxy(settings, id) ? CreateProbeClient(true) : null;
                    await cache.GetAsync(id, token, transport is null ? null : (url, cancellation) =>
                        SubscriptionRefresh.DownloadAsync(new(url, [], null, null, 60, true), url, cancellation, transport)).ConfigureAwait(false);
                    var warnings = podkopWarnings.Where(value => value != id).ToList();
                    if (cache.Unavailable.Contains(id)) warnings.Add(id);
                    if (!podkopWarnings.SequenceEqual(warnings))
                    {
                        podkopWarnings = warnings.ToArray();
                        if (AppliedSettings is { } applied)
                        {
                            var updated = applied.Copy(); SetPodkopWarningMetadata(updated, podkopWarnings); AppliedSettings = updated;
                            RuntimeChanged?.Invoke();
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { Log?.Invoke("Podkop cache refresh failed; active rules were retained: " + error.Message); }
            foreach (var group in PodkopCatalog.Groups(settings).Where(group => group.Length > 1))
                try { cache.MergeGroup(group); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                { Log?.Invoke("Podkop combined rule set could not be refreshed; the previous set was retained."); }
        }
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private static void SetPodkopWarningMetadata(Settings settings, string[] ids) =>
        settings.WindowsOptions[PodkopCatalog.WarningKey] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());

    private ProcessStartInfo StartInfo(string executable, string command)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { command, "-c", configPath }) info.ArgumentList.Add(arg);
        return info;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) { Log?.Invoke(line); ObserveProxyFailure(line); } }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        failoverStopping?.Cancel();
        listStopping?.Cancel();
        job?.Dispose(); job = null;
        consoleLease?.Dispose(); consoleLease = null;
        process?.Dispose();
        client.Dispose();
    }

}
