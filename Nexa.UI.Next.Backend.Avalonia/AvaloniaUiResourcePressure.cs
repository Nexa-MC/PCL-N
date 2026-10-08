using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexa.UI.Next.Backend.Avalonia;

public enum AvaloniaUiMemoryPressure { Unknown, Normal, Elevated, Critical }
public readonly record struct AvaloniaUiMemoryObservation(AvaloniaUiMemoryPressure Pressure,
    long? AvailableBytes, long? LimitBytes, long? GpuResidentBytes, string Source);

/// <summary>Native measurements. GPU residency is never inferred from decoded bitmap sizes.</summary>
public static class AvaloniaUiResourcePressure
{
    public static AvaloniaUiMemoryObservation Read()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var memory = ReadValues("/proc/meminfo", 256);
                if (!memory.TryGetValue("MemTotal", out long totalKiB) || !memory.TryGetValue("MemAvailable", out long availableKiB))
                    return new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable");
                long total = totalKiB * 1024;
                long available = availableKiB * 1024;
                if (total <= 0 || available < 0) return default;
                // cgroup v2 constrains the whole container, so use its actual remaining headroom.
                string? cgroup = File.ReadLines("/proc/self/cgroup").Take(64)
                    .FirstOrDefault(static line => line.StartsWith("0::/", StringComparison.Ordinal))?[4..];
                if (cgroup is not null && !cgroup.Contains("..", StringComparison.Ordinal))
                {
                    string root = Path.Combine("/sys/fs/cgroup", cgroup.TrimStart('/'));
                    if (TryReadNumber(Path.Combine(root, "memory.max"), out long limit)
                        && TryReadNumber(Path.Combine(root, "memory.current"), out long used) && limit > 0)
                    { total = Math.Min(total, limit); available = Math.Min(available, Math.Max(0, limit - used)); }
                }
                return Create(available, total, ReadLinuxGpuResidency(Environment.ProcessId), "linux.meminfo/cgroup-v2/drm-fdinfo");
            }
            if (OperatingSystem.IsWindows())
            {
                MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status))
                    return Create((long)Math.Min(status.AvailablePhysical, long.MaxValue),
                        (long)Math.Min(status.TotalPhysical, long.MaxValue), null, "windows.GlobalMemoryStatusEx");
            }
            if (OperatingSystem.IsMacOS())
            {
                string? vm = RunBounded("/usr/bin/vm_stat", [], 8192);
                if (vm is not null)
                {
                    long pageSize = 4096;
                    string header = vm.Split('\n')[0];
                    int begin = header.IndexOf("page size of ", StringComparison.Ordinal);
                    if (begin >= 0 && !long.TryParse(header[(begin + 13)..].Split(' ')[0], out pageSize))
                        return new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable");
                    if (pageSize <= 0) return new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable");
                    var values = ParseValues(vm.Split('\n').Skip(1));
                    long available = (values.GetValueOrDefault("Pages free") + values.GetValueOrDefault("Pages inactive")
                        + values.GetValueOrDefault("Pages speculative")) * pageSize;
                    if (long.TryParse(RunBounded("/usr/sbin/sysctl", ["-n", "hw.memsize"], 128)?.Trim(), out long total) && total > 0)
                        return Create(available, total, null, "macos.vm_stat/sysctl.hw.memsize");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { }
        return new(AvaloniaUiMemoryPressure.Unknown, null, null, null, "unavailable");
    }

    internal static AvaloniaUiMemoryObservation Create(long available, long limit, long? gpu, string source)
    {
        double fraction = limit > 0 ? (double)available / limit : 1;
        var pressure = available < 128L * 1024 * 1024 || fraction < .05 ? AvaloniaUiMemoryPressure.Critical
            : available < 512L * 1024 * 1024 || fraction < .15 ? AvaloniaUiMemoryPressure.Elevated : AvaloniaUiMemoryPressure.Normal;
        return new(pressure, available, limit, gpu, source);
    }

    internal static long? ReadLinuxGpuResidency(int pid)
    {
        if (!OperatingSystem.IsLinux()) return null;
        long total = 0;
        bool observed = false;
        HashSet<string> clients = new(StringComparer.Ordinal);
        try
        {
            foreach (string path in Directory.EnumerateFiles($"/proc/{pid}/fdinfo").Take(512))
            {
                string[] lines = File.ReadLines(path).Take(128).ToArray();
                string? client = lines.FirstOrDefault(static line => line.StartsWith("drm-client-id:", StringComparison.Ordinal));
                if (client is null || !clients.Add(client + lines.FirstOrDefault(static line => line.StartsWith("drm-pdev:", StringComparison.Ordinal)))) continue;
                foreach (string line in lines)
                {
                    // New DRM fdinfo supplies resident memory; older drm-memory-* is allocated,
                    // so it must not be presented as actual residency.
                    if (!line.StartsWith("drm-resident-", StringComparison.Ordinal)) continue;
                    string value = line[(line.IndexOf(':') + 1)..].Trim();
                    string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !long.TryParse(parts[0], out long number) || number < 0) continue;
                    long unit = value.EndsWith("KiB", StringComparison.Ordinal) ? 1024 : 1;
                    if (number > (long.MaxValue - total) / unit) return null;
                    total += number * unit;
                    observed = true;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return observed ? total : null;
    }

    private static Dictionary<string, long> ReadValues(string path, int maximumLines) => ParseValues(File.ReadLines(path).Take(maximumLines));
    private static Dictionary<string, long> ParseValues(IEnumerable<string> lines)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string value = line[(colon + 1)..].Trim().TrimEnd('.');
            if (long.TryParse(value.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                result[line[..colon]] = number;
        }
        return result;
    }
    private static bool TryReadNumber(string path, out long value)
    {
        value = 0;
        return File.Exists(path) && new FileInfo(path).Length < 256
            && long.TryParse(File.ReadAllText(path).Trim(), out value);
    }
    private static string? RunBounded(string executable, string[] arguments, int maximumBytes)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        char[] output = new char[maximumBytes];
        try
        {
            int count = process.StandardOutput.ReadBlockAsync(output.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return process.ExitCode == 0 && count < maximumBytes ? new string(output, 0, count) : null;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); return null; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

internal sealed class AvaloniaUiResourcePressureSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    internal AvaloniaUiResourcePressureSession() => _ = MonitorAsync(_stop.Token);
    private static async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
            do
            {
                var sample = await Task.Run(AvaloniaUiResourcePressure.Read, token).ConfigureAwait(false);
                AvaloniaUiRasterPool.Shared.ApplyPressure(sample);
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
