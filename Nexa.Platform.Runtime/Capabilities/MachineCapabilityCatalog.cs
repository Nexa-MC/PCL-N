using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nexa.Services.Capabilities;

internal sealed class RuntimeCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineCapabilityCatalog.RuntimeProviderId;
    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string source = ".NET RuntimeInformation / Environment";
        const string isa = ".NET Intrinsics（当前进程可用指令集）";
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            MachineCapabilityCatalog.Os.Observe(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Other", timestamp, source),
            MachineCapabilityCatalog.OsVersion.Observe(RuntimeInformation.OSDescription, timestamp, source),
            MachineCapabilityCatalog.NativeArch.Observe(RuntimeInformation.OSArchitecture.ToString(), timestamp, source),
            MachineCapabilityCatalog.ProcessArch.Observe(RuntimeInformation.ProcessArchitecture.ToString(), timestamp, source),
            MachineCapabilityCatalog.Runtime.Observe(RuntimeInformation.FrameworkDescription, timestamp, source),
            MachineCapabilityCatalog.DynamicCode.Observe(RuntimeFeature.IsDynamicCodeSupported, timestamp, source),
            MachineCapabilityCatalog.LogicalProcessors.Observe(Environment.ProcessorCount, timestamp, source),
            MachineCapabilityCatalog.Sse2.Observe(Sse2.IsSupported, timestamp, isa),
            MachineCapabilityCatalog.Avx2.Observe(Avx2.IsSupported, timestamp, isa),
            MachineCapabilityCatalog.Neon.Observe(AdvSimd.IsSupported, timestamp, isa),
        ]));
    }
}

internal sealed partial class MemoryCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineCapabilityCatalog.MemoryProviderId;
    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            if (GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()) == 0) throw new InvalidOperationException("Memory probe failed.");
            long page = checked((long)info.PageSize);
            return Values(checked((long)info.PhysicalTotal * page), checked((long)info.PhysicalAvailable * page),
                checked((long)info.CommitTotal * page), checked((long)info.CommitLimit * page), timestamp, "Windows GetPerformanceInfo");
        }
        if (OperatingSystem.IsLinux())
        {
            string text = await File.ReadAllTextAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
            return ParseLinux(text, timestamp);
        }
        if (OperatingSystem.IsMacOS())
        {
            return CollectMacOs(timestamp);
        }
        return Array.AsReadOnly(MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == Id)
            .Select(item => item.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, "平台不支持")).ToArray());
    }
    internal static IReadOnlyList<ICapability> CollectMacOs(DateTimeOffset timestamp)
    {
        const string source = "macOS sysctl + host_statistics64";
        List<ICapability> facts = [];
        if (SysctlUlong("hw.memsize", out ulong installed))
        {
            facts.Add(MachineCapabilityCatalog.PhysicalUsable.Observe((long)installed, timestamp, source));
        }
        else
        {
            facts.Add(MachineCapabilityCatalog.PhysicalUsable.Unavailable(CapabilityAvailability.Unknown, timestamp, "sysctl 读取失败"));
        }

        // macOS pages memory instead of committing it: free+inactive approximates what a new
        // heavy process can actually take without swapping the desktop to death.
        if (SysctlUlong("hw.pagesize", out ulong pageSize)
            && HostStatistics64(out long freePages, out long inactivePages))
        {
            facts.Add(MachineCapabilityCatalog.PhysicalAvailable.Observe(
                (long)(pageSize * (ulong)(freePages + inactivePages)), timestamp, source + "（free+inactive）"));
        }
        else
        {
            facts.Add(MachineCapabilityCatalog.PhysicalAvailable.Unavailable(CapabilityAvailability.Unknown, timestamp, "页统计不可读"));
        }

        // The commit budget is a Windows (CommitLimit/CommitTotal) concept; macOS has no
        // commit limit at all, so the facts report platform-unsupported, not zero.
        facts.Add(MachineCapabilityCatalog.CommitTotal.Unavailable(
            CapabilityAvailability.PlatformUnsupported, timestamp, "平台不支持：macOS 无提交预算概念"));
        facts.Add(MachineCapabilityCatalog.CommitLimit.Unavailable(
            CapabilityAvailability.PlatformUnsupported, timestamp, "平台不支持：macOS 无提交预算概念"));
        facts.Add(MachineCapabilityCatalog.CommitAvailable.Unavailable(
            CapabilityAvailability.PlatformUnsupported, timestamp, "平台不支持：macOS 无提交预算概念"));
        return facts;
    }

    private static bool SysctlUlong(string name, out ulong value)
    {
        value = 0;
        // Xlib-style raw UTF-8 bytes for the name: byte buffers sidestep the string
        // marshaling analyzer rules while matching the C ABI exactly.
        byte[] nameBytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        _ = System.Text.Encoding.UTF8.GetBytes(name, 0, name.Length, nameBytes, 0);
        unsafe
        {
            ulong local = 0;
            nuint length = (nuint)sizeof(ulong);
            if (sysctlbyname(nameBytes, &local, &length, 0, 0) != 0)
            {
                return false;
            }

            value = local;
            return true;
        }
    }

    private static bool HostStatistics64(out long freePages, out long inactivePages)
    {
        freePages = 0;
        inactivePages = 0;
        unsafe
        {
            nint host = mach_host_self();
            if (host == 0)
            {
                return false;
            }

            // vm_statistics_data_64: 12 leading natural_t counters; free=0? Layout:
            // free, active, inactive, wire, faults, cow, msgs, swaps...
            uint* stats = stackalloc uint[13];
            nuint count = (nuint)(13 * sizeof(uint));
            const int HostVmInfo64 = 4;
            if (host_statistics64(host, HostVmInfo64, stats, &count) != 0)
            {
                _ = mach_port_deallocate(mach_task_self(), host);
                return false;
            }

            freePages = stats[0];
            inactivePages = stats[2];
            _ = mach_port_deallocate(mach_task_self(), host);
            return true;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int sysctlbyname(byte[] name, ulong* old, nuint* oldLength, nint zero, nint zero2);

    [DllImport("libc", SetLastError = false)]
    private static extern nint mach_host_self();

    [DllImport("libc", SetLastError = false)]
    private static extern nint mach_task_self();

    [DllImport("libc", SetLastError = false)]
    private static extern int mach_port_deallocate(nint task, nint port);

    [DllImport("libc", SetLastError = false)]
    private static extern unsafe int host_statistics64(nint host, int flavor, uint* info, nuint* count);

    internal static IReadOnlyList<ICapability> ParseLinux(string text, DateTimeOffset timestamp)
    {
        var fields = text.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], StringComparer.Ordinal);
        long Read(string name) => checked(long.Parse(fields[name], CultureInfo.InvariantCulture) * 1024);
        return Values(Read("MemTotal"), Read("MemAvailable"), Read("Committed_AS"), Read("CommitLimit"), timestamp,
            "Linux /proc/meminfo（提交上限受 overcommit 策略影响）");
    }
    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> Values(long physical, long available, long committed, long limit, DateTimeOffset timestamp, string source) =>
        Array.AsReadOnly<ICapability>([
            MachineCapabilityCatalog.PhysicalUsable.Observe(physical, timestamp, source),
            MachineCapabilityCatalog.PhysicalAvailable.Observe(available, timestamp, source),
            MachineCapabilityCatalog.CommitTotal.Observe(committed, timestamp, source),
            MachineCapabilityCatalog.CommitLimit.Observe(limit, timestamp, source),
            MachineCapabilityCatalog.CommitAvailable.Observe(Math.Max(0, limit - committed), timestamp, source),
        ]);
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [LibraryImport("psapi.dll", SetLastError = true)]
    private static partial int GetPerformanceInfo(out PerformanceInformation information, uint size);
}
