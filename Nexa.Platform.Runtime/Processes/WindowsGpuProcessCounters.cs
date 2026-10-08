using System.Runtime.InteropServices;

namespace Nexa.Platform;

/// <summary>Windows driver process memory counters; absent instances retain unknown.</summary>
internal static class WindowsGpuProcessCounters
{
    internal static (long? Local, long? Shared) Read(int processId)
    {
        if (!OperatingSystem.IsWindows() || PdhOpenQuery(null, 0, out nint query) != 0) return (null, null);
        try
        {
            long? ReadCounter(string counterName)
            {
                string path = $"\\GPU Process Memory(pid_{processId}_*)\\{counterName}";
                if (PdhAddEnglishCounter(query, path, 0, out nint counter) != 0 || PdhCollectQueryData(query) != 0) return null;
                uint bytes = 0, count = 0;
                uint result = PdhGetFormattedCounterArray(counter, 0x400, ref bytes, ref count, 0);
                if (result != 0x800007D2 || bytes is < 1 or > 65536 || count is < 1 or > 1024) return null;
                nint buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (PdhGetFormattedCounterArray(counter, 0x400, ref bytes, ref count, buffer) != 0) return null;
                    long total = 0;
                    int size = Marshal.SizeOf<CounterItem>();
                    if ((long)size * count > bytes) return null;
                    for (int index = 0; index < count; index++)
                    {
                        CounterItem item = Marshal.PtrToStructure<CounterItem>(buffer + index * size);
                        if (item.Value.Status > 1 || item.Value.Large < 0 || item.Value.Large > long.MaxValue - total) return null;
                        total += item.Value.Large;
                    }
                    return total;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return (ReadCounter("Dedicated Usage"), ReadCounter("Shared Usage"));
        }
        finally { _ = PdhCloseQuery(query); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public long Large; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public nint Name; public CounterValue Value; }
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, nuint userData, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bytes, ref uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
