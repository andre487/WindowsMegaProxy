using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsMegaProxy;

internal static class PerformanceChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync()
    {
        var initial = new Settings();
        var profile = ProxyProfile.Create("Synthetic"); profile.Proxy["host"] = "proxy.example.com";
        initial.Profiles.Add(profile); initial.ActiveProfileId = profile.Id;
        initial.Rules.Add(new RoutingRule { Path = "original" });
        initial.SharedOptions["foreign"] = new System.Text.Json.Nodes.JsonObject { ["value"] = "original" };
        var copy = initial.Copy();
        copy.Profiles[0].Proxy["host"] = "changed.example.com";
        copy.Rules[0].Path = "changed";
        copy.SharedOptions["foreign"]!["value"] = "changed";
        Assert(initial.Profiles[0].Host == "proxy.example.com" && initial.Rules[0].Path == "original" && initial.SharedOptions["foreign"]!["value"]!.ToString() == "original", "Snapshot copying isolates all mutable data, including foreign fields.");
        var prepared = SettingsAnalysis.Prepare(initial);
        var uiOnly = initial.Copy(); uiOnly.Theme = "dark"; uiOnly.ShowAllConnections = true;
        Assert(ReferenceEquals(prepared.Signature, SettingsAnalysis.Prepare(uiOnly, prepared).Signature), "UI-only changes reuse the computed network analysis.");
        var profiles = Enumerable.Range(0, 1000).Select(_ => ProxyProfile.Create()).ToArray();
        _ = ProfileAppearance.Colors;
        long before = GC.GetAllocatedBytesForCurrentThread();
        ProfileAppearance.AssignMissingColors(profiles);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated < 4 * 1024 * 1024, "Assigning colors to 1,000 profiles must not allocate hundreds of MiB.");
        Assert(profiles.GroupBy(item => item.ColorIndex).Max(group => group.Count()) - profiles.GroupBy(item => item.ColorIndex).Min(group => group.Count()) <= 1, "Color assignment keeps the existing balanced distribution.");

        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<int>();
        int callerThread = Environment.CurrentManagedThreadId;
        bool onCaller = false;
        var writer = new SettingsWriter(settings =>
        {
            onCaller |= Environment.CurrentManagedThreadId == callerThread;
            int value = settings.LogSizeMb;
            if (value == 1) { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
            writes.Add(value);
        });
        Task<SettingsAnalysis> Save(int value) => writer.SaveAsync(new Settings { LogSizeMb = value });
        var first = Save(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Save(2); var third = Save(3);
        release.Set();
        await Task.WhenAll(first, second, third);
        await writer.FlushAsync();
        Assert(!onCaller && writes.SequenceEqual(new[] { 1, 3 }), "Disk writes run in sequence on a worker and combine pending edits without writing stale state last.");
        bool fail = true;
        var failing = new SettingsWriter(_ => { if (fail) throw new IOException("Synthetic persistence failure."); });
        try { await failing.SaveAsync(new Settings()); throw new Exception("Save failure was hidden."); }
        catch (IOException) { }
        fail = false;
        await failing.SaveAsync(new Settings());
        await failing.FlushAsync();

        string directory = Path.GetFullPath("artifacts/background-log-" + Guid.NewGuid().ToString("N"));
        var log = new DiagnosticLog(directory);
        await using var background = new BackgroundLog(log);
        await background.SetLimitAsync(1);
        for (int i = 0; i < 100; i++) background.Write("Entry " + i + " password=secret");
        string text = await background.ReadAsync();
        Assert(text.Contains("Entry 99") && !text.Contains("password=secret") && text.IndexOf("Entry 0 ", StringComparison.Ordinal) < text.IndexOf("Entry 99 ", StringComparison.Ordinal), "Background log flush preserves ordering and secret redaction.");
        string exported = Path.Combine(directory, "export.log");
        await background.ExportAsync(exported);
        Assert(await File.ReadAllTextAsync(exported) == text && Directory.GetFiles(directory, "*.tmp").Length == 0, "Streaming export preserves the log snapshot and removes its temporary file.");
        string history = Path.Combine(directory, "startup-history");
        Directory.CreateDirectory(history);
        foreach (string name in new[] { "current.log", "previous.log" })
            await File.WriteAllTextAsync(Path.Combine(history, name), new string('x', 3 * 1024 * 1024));
        new DiagnosticLog(history, initialLimit: 100).Write("Startup before loading the saved log limit.");
        Assert(new FileInfo(Path.Combine(history, "previous.log")).Length == 3 * 1024 * 1024 &&
            new FileInfo(Path.Combine(history, "current.log")).Length > 3 * 1024 * 1024, "Startup cannot rotate large existing logs using a temporary default limit.");
    }
}
