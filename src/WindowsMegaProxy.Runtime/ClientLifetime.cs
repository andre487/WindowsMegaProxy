using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsMegaProxy;

public sealed class ClientLifetime : IDisposable
{
    private readonly SafeWaitHandle process;
    private ClientLifetime(SafeWaitHandle process) => this.process = process;
    public bool IsAlive => !process.IsInvalid && WaitForSingleObject(process, 0) == 258;
    public static ClientLifetime Open(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // A process handle identifies this lifetime even if Windows later reuses the PID.
        return new(OpenProcess(0x100000, false, pid));
    }
    public void Dispose() => process.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
}
