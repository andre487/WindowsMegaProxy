using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsMegaProxy;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class CoreProcess
{
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token, byte[]? input = null)
    {
        token.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.RedirectStandardInput = input is not null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var job = new ProcessJob();
        using var process = Process.Start(start) ?? throw new IOException("Could not start the core command.");
        try
        {
            job.Assign(process);
            var output = ReadAsync(process.StandardOutput, deadline);
            var error = ReadAsync(process.StandardError, deadline);
            if (input is not null)
            {
                try { await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token); }
                catch (IOException) when (process.HasExited) { }
                finally { process.StandardInput.Close(); }
            }
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            deadline.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        try
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (text.Length + count > 65536) throw new IOException("Core command output exceeds the size limit.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch { deadline.Cancel(); throw; }
    }
}

internal sealed class ProcessJob : IDisposable
{
    private IntPtr handle;
    public ProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobExtendedLimit { BasicLimitInformation = new JobBasicLimit { LimitFlags = 0x2000 } };
        if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimit>()))
        {
            Dispose();
            throw new IOException("Could not protect the core process lifetime.");
        }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle) && !process.HasExited)
            throw new IOException("Could not attach the core process to the application.");
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
