using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nexa.Platform;

public sealed class PlatformProcessControl : IPlatformProcessControl
{
    public PlatformProcessControlResult Suspend(Process process, bool suspend) => ChangeSuspension(process, suspend);

    public PlatformProcessControlResult SetPriority(Process process, ProcessPriorityClass priority)
    {
        ArgumentNullException.ThrowIfNull(process);
        try { process.PriorityClass = priority; return new(true, "ok", "进程优先级已更新。"); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(false, "priority_unavailable", exception.Message); }
    }

    public PlatformProcessControlResult SetAffinity(Process process, nint affinityMask)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (affinityMask == 0) return new(false, "invalid_affinity", "处理器亲和掩码不能为零。");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return new(false, "platform_unsupported", "当前平台不支持处理器亲和性。");
        try { process.ProcessorAffinity = affinityMask; return new(true, "ok", "处理器亲和性已更新。"); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(false, "affinity_unavailable", exception.Message); }
    }

    private static PlatformProcessControlResult ChangeSuspension(Process process, bool suspend)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            int result;
            if (OperatingSystem.IsWindows())
                result = suspend ? NtSuspendProcess(process.Handle) : NtResumeProcess(process.Handle);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                result = kill(process.Id, OperatingSystem.IsMacOS() ? (suspend ? 17 : 19) : (suspend ? 19 : 18));
            else return new(false, "platform_unsupported", "当前平台不支持进程暂停。");
            return result == 0 ? new(true, "ok", suspend ? "进程已暂停。" : "进程已恢复。")
                : new(false, "control_failed", "操作系统拒绝了进程控制请求。");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, "control_failed", exception.Message); }
    }

    public PlatformProcessSample ReadSample(Process process)
    {
        process.Refresh();
        long read = 0, write = 0;
        if (OperatingSystem.IsWindows() && GetProcessIoCounters(process.Handle, out IoCounters counters))
        {
            read = checked((long)Math.Min(counters.ReadTransferCount, long.MaxValue));
            write = checked((long)Math.Min(counters.WriteTransferCount, long.MaxValue));
        }
        return new(process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count, process.TotalProcessorTime, read, write);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint processHandle, out IoCounters counters);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint processHandle);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint processHandle);
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
