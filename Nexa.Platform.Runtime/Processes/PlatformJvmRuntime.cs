using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexa.Platform;

/// <summary>Actual OS providers; missing or denied counters retain nullable unknown values.</summary>
public sealed class PlatformJvmRuntime : IPlatformJvmRuntime
{
    public ValueTask<PlatformJvmMemoryOutput> ReadJvmMemoryAsync(string javaExecutable, int processId)
        => PlatformJvmMemoryProbe.ReadAsync(javaExecutable, processId);
    public bool CpuSetsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    public bool QualitySupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299);
    public bool CommitSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public bool GpuSupported => OperatingSystem.IsWindowsVersionAtLeast(10) || OperatingSystem.IsLinux() && Directory.Exists("/sys/class/drm")
        && Directory.EnumerateDirectories("/sys/class/drm", "card*").Any();
    public bool TreeSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();
    public bool SystemEventsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && File.Exists("/usr/bin/journalctl")
        || OperatingSystem.IsMacOS() && File.Exists("/usr/bin/log");

    public PlatformProcessControlResult SetCpuSets(Process process, IReadOnlyList<int> logicalProcessors)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logicalProcessors);
        if (logicalProcessors.Count is < 1 or > 1024 || logicalProcessors.Any(static cpu => cpu is < 0 or >= 1024)
            || logicalProcessors.Distinct().Count() != logicalProcessors.Count)
            return new(false, "invalid_cpu_set", "CPU 集合必须包含 1–1024 个不同的逻辑处理器编号。");
        try
        {
            if (OperatingSystem.IsLinux())
            {
                byte[] mask = new byte[128];
                foreach (int cpu in logicalProcessors) mask[cpu / 8] |= (byte)(1 << (cpu % 8));
                return Result(sched_setaffinity(process.Id, (nuint)mask.Length, mask) == 0, "CPU 集合已更新。");
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var masks = logicalProcessors.GroupBy(static cpu => cpu / 64).Select(group =>
                    new GroupAffinity { Group = (ushort)group.Key, Mask = (nuint)group.Aggregate(0UL, static (mask, cpu) => mask | 1UL << (cpu % 64)) }).ToArray();
                return Result(SetProcessDefaultCpuSetMasks(process.Handle, masks, (ushort)masks.Length), "CPU 集合已更新。");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
            or DllNotFoundException or EntryPointNotFoundException)
        { return new(false, "control_unavailable", error.Message); }
        return new(false, "platform_unsupported", "当前系统不支持逻辑处理器集合控制。");
    }

    public PlatformProcessControlResult SetQuality(Process process, PlatformProcessQuality quality)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!Enum.IsDefined(quality)) return new(false, "invalid_quality", "未知服务质量策略。");
        try
        {
            if (OperatingSystem.IsLinux())
                return Result(setpriority(0, process.Id, quality switch { PlatformProcessQuality.Background => 10, PlatformProcessQuality.Efficiency => 19, _ => 0 }) == 0,
                    "进程调度权重已更新。");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            {
                PowerThrottling state = new() { Version = 1, ControlMask = 1, StateMask = quality == PlatformProcessQuality.Default ? 0u : 1u };
                return Result(SetProcessInformation(process.Handle, 4, ref state, (uint)Marshal.SizeOf<PowerThrottling>()), "进程 EcoQoS 已更新。");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
            or DllNotFoundException or EntryPointNotFoundException)
        { return new(false, "control_unavailable", error.Message); }
        return new(false, "platform_unsupported", "当前系统不支持进程服务质量控制。");
    }

    public PlatformJvmSample ReadSample(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        long? commit = null, gpuLocal = null, gpuShared = null, working = null, cpu = null;
        int? count = null;
        if (OperatingSystem.IsLinux())
        {
            commit = ReadLinuxCommit(process.Id);
            (gpuLocal, gpuShared) = ReadLinuxGpu(process.Id);
        }
        else if (OperatingSystem.IsWindows())
        {
            MemoryCounters counters = new() { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            if (GetProcessMemoryInfo(process.Handle, ref counters, counters.Size)) commit = (long)counters.PrivateUsage;
            (gpuLocal, gpuShared) = WindowsGpuProcessCounters.Read(process.Id);
        }
        if (TreeSupported)
        {
            var tree = ReadTree(process.Id);
            if (tree is not null)
            {
                long totalWorking = 0, totalCpu = 0;
                bool complete = true;
                foreach (int pid in tree)
                {
                    try
                    {
                        using var child = Process.GetProcessById(pid);
                        totalWorking += child.WorkingSet64;
                        totalCpu += (long)child.TotalProcessorTime.TotalMilliseconds;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                    { complete = false; }
                }
                if (complete) { working = totalWorking; cpu = totalCpu; count = tree.Count; }
            }
        }
        return new(commit, gpuLocal, gpuShared, working, cpu, count,
            OperatingSystem.IsLinux() ? "linux.smaps-accountable/drm-fdinfo/proc-tree" : "windows.psapi/pdh-gpu-process/toolhelp");
    }

    internal static long? ReadLinuxCommit(int pid)
    {
        try
        {
            using var reader = new StreamReader($"/proc/{pid}/smaps");
            long total = 0, size = 0;
            int characters = 0;
            bool measured = false;
            while (reader.ReadLine() is { } line)
            {
                if ((characters += line.Length) > 4 * 1024 * 1024) return null;
                if (line.StartsWith("Size:", StringComparison.Ordinal))
                {
                    if (!TryParseKilobytes(line, out size)) return null;
                }
                else if (line.StartsWith("VmFlags:", StringComparison.Ordinal))
                {
                    if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("ac", StringComparer.Ordinal)) total += size;
                    measured = true;
                }
            }
            return measured ? total : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static (long? Local, long? Shared) ReadLinuxGpu(int pid)
    {
        long local = 0, shared = 0;
        bool localRead = false, sharedRead = false;
        HashSet<string> clients = new(StringComparer.Ordinal);
        try
        {
            foreach (string file in Directory.EnumerateFiles($"/proc/{pid}/fdinfo").Take(512))
            {
                string[] lines = File.ReadLines(file).Take(128).ToArray();
                string? client = lines.FirstOrDefault(static line => line.StartsWith("drm-client-id:", StringComparison.Ordinal));
                if (client is null || !clients.Add(client + lines.FirstOrDefault(static line => line.StartsWith("drm-pdev:", StringComparison.Ordinal)))) continue;
                foreach (string line in lines)
                {
                    if (line.StartsWith("drm-resident-vram:", StringComparison.Ordinal) && TryParseKilobytes(line, out long localValue))
                    { if (localValue > long.MaxValue - local) return (null, null); local += localValue; localRead = true; }
                    else if (line.StartsWith("drm-resident-gtt:", StringComparison.Ordinal) || line.StartsWith("drm-resident-system:", StringComparison.Ordinal))
                    { if (TryParseKilobytes(line, out long sharedValue)) { if (sharedValue > long.MaxValue - shared) return (null, null); shared += sharedValue; sharedRead = true; } }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, null); }
        return (localRead ? local : null, sharedRead ? shared : null);
    }

    private static HashSet<int>? ReadTree(int root)
    {
        Dictionary<int, int> parents = [];
        if (OperatingSystem.IsLinux())
        {
            int seen = 0;
            try
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                    if (++seen > 8192) return null;
                    try
                    {
                        string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                        int end = stat.LastIndexOf(')');
                        if (end >= 0 && int.TryParse(stat[(end + 2)..].Split(' ')[1], out int parent)) parents[pid] = parent;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { return null; }
        }
        else
        {
            nint snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == -1) return null;
            try
            {
                ProcessEntry entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32First(snapshot, ref entry)) return null;
                do
                {
                    if (parents.Count >= 8192) return null;
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                } while (Process32Next(snapshot, ref entry));
            }
            finally { CloseHandle(snapshot); }
        }
        HashSet<int> result = [root];
        bool changed;
        do
        {
            changed = false;
            foreach ((int pid, int parent) in parents)
                if (result.Contains(parent)) changed |= result.Add(pid);
        } while (changed);
        return result;
    }

    public async ValueTask<IReadOnlyList<PlatformSystemEvent>?> ReadSystemEventsAsync(int processId,
        DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        if (!SystemEventsSupported || processId < 1 || until < since || until - since > TimeSpan.FromDays(7)) return null;
        if (OperatingSystem.IsWindows())
            return await WindowsSystemEventCorrelation.ReadAsync(processId, since, until, cancellationToken).ConfigureAwait(false);
        string start = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string end = until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        ProcessStartInfo info = OperatingSystem.IsWindows()
            ? new("wevtutil.exe") : new(OperatingSystem.IsMacOS() ? "/usr/bin/log" : "/usr/bin/journalctl");
        info.UseShellExecute = false; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        string[] arguments = OperatingSystem.IsWindows()
            ? ["qe", "System", $"/q:*[System[Execution[@ProcessID='{processId}'] and TimeCreated[@SystemTime>='{start}' and @SystemTime<='{end}']]]", "/f:text", "/c:32"]
            : OperatingSystem.IsMacOS() ? ["show", "--style", "compact", "--start", since.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "--end", until.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "--predicate", $"processIdentifier == {processId}"]
            : ["--no-pager", "--output=short-iso", "--since=" + start, "--until=" + end, "_PID=" + processId.ToString(CultureInfo.InvariantCulture), "--lines=32"];
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            try
            {
                char[] output = new char[32768];
                int count = await process.StandardOutput.ReadBlockAsync(output.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0 || count == output.Length) return null;
                return new string(output, 0, count).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(32)
                    .Where(static line => !line.StartsWith("--", StringComparison.Ordinal))
                    .Select(line => new PlatformSystemEvent(DateTimeOffset.TryParse(line.Split(' ')[0], CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) ? date : null,
                        Path.GetFileName(info.FileName), line.Length <= 1024 ? line : line[..1024])).ToArray();
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool TryParseKilobytes(string line, out long bytes)
    {
        bytes = 0;
        string[] fields = line[(line.IndexOf(':') + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[1] is not ("kB" or "KiB") || !long.TryParse(fields[0],
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) || number < 0 || number > long.MaxValue / 1024) return false;
        bytes = number * 1024;
        return true;
    }
    private static PlatformProcessControlResult Result(bool success, string message) => success ? new(true, "ok", message)
        : new(false, "os_denied", $"操作系统拒绝请求（错误 {Marshal.GetLastPInvokeError()}）。");
    [StructLayout(LayoutKind.Sequential)] private struct GroupAffinity { public nuint Mask; public ushort Group, Reserved0, Reserved1, Reserved2; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonPagedPool, QuotaNonPagedPool, PageFile, PeakPageFile, PrivateUsage;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint Heap;
        public uint ModuleId, Threads, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("libc", SetLastError = true)] private static extern int sched_setaffinity(int pid, nuint size, byte[] mask);
    [DllImport("libc", SetLastError = true)] private static extern int setpriority(int which, int who, int priority);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSetMasks(nint process, GroupAffinity[] masks, ushort count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int kind, ref PowerThrottling state, uint size);
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
