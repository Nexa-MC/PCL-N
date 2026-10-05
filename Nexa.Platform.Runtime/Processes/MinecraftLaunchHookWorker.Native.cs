using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexa.Services.Minecraft.Process;

public static partial class MinecraftLaunchHookWorker
{
    private static void CreateGroup()
    {
        if (SetSessionId() < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal static void KillGroup(int processId)
    {
        if (Kill(-processId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    internal sealed class WindowsJob : IDisposable
    {
        private readonly SafeFileHandle _handle;
        internal WindowsJob()
        {
            nint handle = CreateJobObject(0, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _handle = new(handle, ownsHandle: true);
            try { SetKillOnClose(true); }
            catch { _handle.Dispose(); throw; }
        }

        private WindowsJob(SafeFileHandle handle) { _handle = handle; }

        internal static WindowsJob FromTransferredHandle(long value)
        {
            if (value <= 0) throw new InvalidDataException("Missing hook Job ownership.");
            var handle = new SafeFileHandle(checked((nint)value), ownsHandle: true);
            try
            {
                if (!IsProcessInJob(GetCurrentProcess(), handle, out bool assigned) || !assigned)
                    throw new InvalidDataException("The hook worker was not assigned to its Job.");
                return new(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        internal void Assign(System.Diagnostics.Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        internal long DuplicateTo(System.Diagnostics.Process process)
        {
            if (!DuplicateHandle(GetCurrentProcess(), _handle, process.SafeHandle, out nint copy, 0, false, 2))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return copy;
        }

        internal unsafe void SetKillOnClose(bool enabled)
        {
            ExtendedLimits limits = new() { Basic = new() { LimitFlags = enabled ? 0x2000u : 0u } };
            if (!SetInformationJobObject(_handle, 9, &limits, (uint)sizeof(ExtendedLimits)))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        internal void Terminate()
        {
            if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime;
        public long JobUserTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int SetSessionId();
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial nint CreateJobObject(nint security, nint name);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool assigned);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, SafeProcessHandle targetProcess,
        out nint copy, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ExtendedLimits* information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
