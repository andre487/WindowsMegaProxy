using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record SshHostKeyUpdate(string ProfileId, string ProfileName, string Host, int Port, string PreviousFingerprint, string Fingerprint, string PublicKey);

public sealed class Engine : IDisposable
{
    private Process? process;
    private HttpClient client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private string configPath = "";
    private readonly string stateDirectory;
    private readonly Uri http3ProbeUri;
    private readonly ConsoleHandler consoleHandler = signal => signal == 0;
    private IntPtr job;
    public bool Running => process is { HasExited: false };
    public event Action? Exited;
    public event Action<string>? Log;
    public Settings? AppliedSettings { get; private set; }
    public string? AppliedSignature { get; private set; }
    public bool Http3Selected { get; private set; }
    public bool UsedHttp3Fallback { get; private set; }
    public Func<SshHostKeyUpdate, Task<bool>>? ConfirmSshHostKeyChange { get; set; }
    public Action<SshHostKeyUpdate>? SaveSshHostKey { get; set; }
    private string[] outboundTags = [];
    private int probePort;
    private string probeSecret = "";
    private Task drain = Task.CompletedTask;
    public string SelectedOutbound
    {
        get
        {
            string active = "profile:" + AppliedSettings?.ActiveProfileId;
            return outboundTags.Contains("proxy") ? "proxy" : outboundTags.Contains(active) ? active : outboundTags.FirstOrDefault() ?? "direct";
        }
    }
    public bool HasProbeProfile => SelectedOutbound != "direct";

    public Engine(string? stateDirectory = null, Uri? http3ProbeUri = null)
    {
        this.stateDirectory = stateDirectory ?? SettingsStore.DirectoryPath;
        this.http3ProbeUri = http3ProbeUri ?? new Uri("https://example.com/");
        // Isolate Ctrl+C from development shells; the console belongs only to this manager and its core.
        FreeConsole();
        if (!AllocConsole()) throw new InvalidOperationException("Could not create the core control console.");
        ShowWindow(GetConsoleWindow(), 0);
        SetConsoleCtrlHandler(consoleHandler, true);
    }

    public static string Signature(Settings settings) => new JsonObject
    {
        ["configuration"] = Configuration.Build(settings, 1, "snapshot", allowUnresolvedSsh: true),
        ["tcpFingerprint"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type is "HTTPS" or "HTTPS_JUMP" ? settings.TlsFingerprint : null,
        ["transport"] = Configuration.UsesProxy(settings) ? settings.ActiveProfile?.Type : null,
        ["preferHttp3"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.PreferHttp3 == true,
        ["sshFingerprint"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type == "SSH" ? settings.ActiveProfile.Proxy["trustedHostKey"]?.DeepClone() : null,
        ["acceptAnySshKey"] = Configuration.UsesProxy(settings) && settings.ActiveProfile?.Type == "SSH" && (settings.ActiveProfile.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false)
    }.ToJsonString();

    public async Task StartAsync(Settings settings, bool testOnly = false, CancellationToken cancellationToken = default)
    {
        if (Running) return;
        UsedHttp3Fallback = false;
        var runtime = PortableConfiguration.Copy(settings);
        if (Configuration.UsesProxy(runtime) && runtime.ActiveProfile is { Type: "SSH" } ssh && !(ssh.Proxy["acceptAnyHostKey"]?.GetValue<bool>() ?? false))
        {
            string previous = ssh.Proxy["trustedHostKey"]?.GetValue<string>() ?? "";
            string key = await SshConfiguration.ReadHostKeyAsync(ssh.Host, ssh.Port, previous, cancellationToken, allowChangedKey: true);
            string fingerprint = SshConfiguration.Fingerprint(key);
            var update = new SshHostKeyUpdate(ssh.Id, ssh.Name, ssh.Host, ssh.Port, previous, fingerprint, key);
            if (previous.Length > 0 && previous != fingerprint && (ConfirmSshHostKeyChange is null || !await ConfirmSshHostKeyChange(update)))
                throw new InvalidOperationException("The SSH server key changed. Connection cancelled.");
            if (previous != fingerprint || SshConfiguration.PublicKey(ssh) != key) SaveSshHostKey?.Invoke(update);
            if (ssh.Data["windows"] is not JsonObject) ssh.Data["windows"] = new JsonObject();
            ssh.Data["windows"]!["sshHostKey"] = key;
            ssh.Proxy["trustedHostKey"] = fingerprint;
            Log?.Invoke(previous.Length == 0 ? "Accepted and pinned a new SSH server key." : "Verified the SSH server key.");
        }
        if (Configuration.RequiresHttp3(settings) && settings.ActiveProfile!.PreferHttp3)
        {
            var config = Configuration.Build(runtime, 1, "probe");
            config["inbounds"] = new JsonArray();
            config["route"]!["final"] = "proxy";
            config.Remove("experimental");
            config["log"] = new JsonObject { ["disabled"] = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            Log?.Invoke("Checking HTTP/3 proxy availability without TUN.");
            var result = await RunCoreToolAsync(CoreExecutable(http3: true), config, http3ProbeUri.ToString(), deadline.Token);
            if (result.ExitCode != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Http3Fallback.IsUnavailable([result.Error]))
                {
                    Log?.Invoke("HTTP/3 selection failed; refusing automatic TCP fallback.");
                    throw new HttpRequestException("HTTP/3 proxy connection failed. Check the certificate, credentials and server compatibility.");
                }
                runtime.ActiveProfile!.Proxy["preferHttp3"] = false;
                UsedHttp3Fallback = true;
                Log?.Invoke("HTTP/3 unavailable; selecting the stable core and HTTPS over TCP.");
            }
            else Log?.Invoke("HTTP/3 selected; the proxy test request succeeded.");
        }
        await StartCoreAsync(runtime, testOnly, cancellationToken);
        AppliedSettings = PortableConfiguration.Copy(settings);
        if (runtime.ActiveProfile is { Type: "SSH" } appliedSsh && AppliedSettings.ActiveProfile is { } savedSsh)
        {
            if (savedSsh.Data["windows"] is not JsonObject) savedSsh.Data["windows"] = new JsonObject();
            savedSsh.Data["windows"]!["sshHostKey"] = appliedSsh.Data["windows"]?["sshHostKey"]?.DeepClone();
            savedSsh.Proxy["trustedHostKey"] = appliedSsh.Proxy["trustedHostKey"]?.DeepClone();
        }
        AppliedSignature = Signature(AppliedSettings);
    }

    private static string CoreExecutable(bool http3) => Environment.GetEnvironmentVariable(http3 ? "WINDOWSMEGAPROXY_H3_CORE" : "WINDOWSMEGAPROXY_CORE")
        ?? Path.Combine(AppContext.BaseDirectory, "dependencies", http3 ? "sing-box-h3" : "sing-box", "sing-box.exe");

    private async Task StartCoreAsync(Settings settings, bool testOnly, CancellationToken cancellationToken)
    {
        if (Running) return;
        Http3Selected = Configuration.RequiresHttp3(settings);
        var executable = CoreExecutable(Http3Selected);
        if (!File.Exists(executable)) throw new FileNotFoundException("The core is not installed. For development run setup.cmd and run.cmd; for installation run install-dependencies.cmd with the app directory.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var config = Configuration.Build(settings, port, secret);
        AppliedSettings = PortableConfiguration.Copy(settings);
        AppliedSignature = Signature(settings);
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
        Log?.Invoke($"Starting {(Http3Selected ? Configuration.Http3CoreVersion : Configuration.CoreVersion)} core; HTTP/3={Http3Selected}; TUN={!testOnly}.");
        client.Dispose();
        client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        configPath = Path.Combine(stateDirectory, testOnly ? $"test-runtime-{Guid.NewGuid():N}.json" : "runtime.json");
        try
        {
            await File.WriteAllTextAsync(configPath, config.ToJsonString(Configuration.JsonOptions));
            using (var check = Process.Start(StartInfo(executable, "check")) ?? throw new InvalidOperationException("The core did not start."))
            {
                var output = check.StandardOutput.ReadToEndAsync();
                var error = check.StandardError.ReadToEndAsync();
                await check.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(output, error);
                if (check.ExitCode != 0)
                {
                    Log?.Invoke(await error);
                    throw new InvalidOperationException("sing-box rejected the configuration. Check the profile and rules.");
                }
            }
            job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobExtendedLimit { BasicLimitInformation = new JobBasicLimit { LimitFlags = 0x2000 } };
            if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimit>()))
                throw new InvalidOperationException("Could not enable core shutdown on application failure.");
            process = Process.Start(StartInfo(executable, "run")) ?? throw new InvalidOperationException("The core did not start.");
            if (!AssignProcessToJobObject(job, process.Handle)) throw new InvalidOperationException("Could not attach the core to the application.");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Exited?.Invoke();
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
        catch { await StopAsync(); throw; }
    }

    public async Task<ConnectionSnapshot> GetConnectionsAsync(bool showAll)
    {
        using var response = await client.GetAsync("connections");
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new IOException("Empty core response.");
        return ConnectionSnapshot.Parse(root, AppliedSettings!, showAll);
    }

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
        try { return (await RunCoreToolAsync(CoreExecutable(http3: false), config, url, deadline.Token)).ExitCode == 0; }
        catch (Exception error) when (error is IOException || error is OperationCanceledException && !token.IsCancellationRequested) { return false; }
    }

    private async Task<(int ExitCode, string Error)> RunCoreToolAsync(string executable, JsonObject config, string url, CancellationToken token)
    {
        string path = Path.Combine(stateDirectory, $"http3-probe-{Guid.NewGuid():N}.json");
        Process? fetch = null;
        IntPtr toolJob = IntPtr.Zero;
        try
        {
            await File.WriteAllTextAsync(path, config.ToJsonString(), token);
            toolJob = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobExtendedLimit { BasicLimitInformation = new JobBasicLimit { LimitFlags = 0x2000 } };
            if (toolJob == IntPtr.Zero || !SetInformationJobObject(toolJob, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimit>()))
                throw new IOException("Could not protect the diagnostic process lifetime.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (string argument in new[] { "-c", path, "--disable-color", "tools", "fetch", url }) start.ArgumentList.Add(argument);
            fetch = Process.Start(start) ?? throw new IOException("Could not start HTTP/3 diagnostics.");
            if (!AssignProcessToJobObject(toolJob, fetch.Handle)) throw new IOException("Could not attach HTTP/3 diagnostics to the application.");
            var output = ReadToolOutputAsync(fetch.StandardOutput, capture: false, token);
            var errors = ReadToolOutputAsync(fetch.StandardError, capture: true, token);
            await Task.WhenAll(output, errors, fetch.WaitForExitAsync(token));
            return (fetch.ExitCode, await errors);
        }
        finally
        {
            if (fetch is not null)
            {
                if (!fetch.HasExited) { fetch.Kill(true); await fetch.WaitForExitAsync(); }
                fetch.Dispose();
            }
            if (toolJob != IntPtr.Zero) CloseHandle(toolJob);
            File.Delete(path);
        }
    }

    private static async Task<string> ReadToolOutputAsync(StreamReader reader, bool capture, CancellationToken token)
    {
        var buffer = new char[4096];
        var text = new System.Text.StringBuilder();
        int total = 0, count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if ((total += count) > 65536) throw new IOException("HTTP/3 diagnostic output exceeds the size limit.");
            if (capture) text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public async Task StopAsync()
    {
        if (Running)
        {
            GenerateConsoleCtrlEvent(0, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process!.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (Running) { process!.Kill(true); await process.WaitForExitAsync(); } }
        }
        await drain;
        process?.Dispose();
        process = null;
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (configPath.Length > 0) File.Delete(configPath);
        Log?.Invoke("Network core stopped.");
    }

    private ProcessStartInfo StartInfo(string executable, string command)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { command, "-c", configPath }) info.ArgumentList.Add(arg);
        return info;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is { } line) Log?.Invoke(line); }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (job != IntPtr.Zero) CloseHandle(job);
        process?.Dispose();
        client.Dispose();
        SetConsoleCtrlHandler(consoleHandler, false);
    }

    private delegate bool ConsoleHandler(uint signal);
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleHandler handler, bool add);
    [DllImport("kernel32.dll")] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
