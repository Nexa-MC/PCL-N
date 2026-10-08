using System.Globalization;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Optional JDK attach provider. A JRE or disabled NMT yields unknown, never zero.</summary>
internal static class JvmMemoryProbe
{

    internal static long? ParseHeap(string? output)
    {
        if (output is null) return null;
        long sum = 0;
        bool observed = false;
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains("Metaspace", StringComparison.OrdinalIgnoreCase) || line.Contains("class space", StringComparison.OrdinalIgnoreCase)) continue;
            int marker = line.IndexOf("used ", StringComparison.Ordinal);
            if (marker < 0 || !TryQuantity(line[(marker + 5)..], out long value)) continue;
            sum += value;
            observed = true;
        }
        return observed ? sum : null;
    }
    internal static long? ParseNative(string? output)
    {
        if (output is null) return null;
        long? total = null, heap = null;
        foreach (string line in output.Split('\n'))
        {
            int marker = line.IndexOf("committed=", StringComparison.Ordinal);
            if (marker < 0 || !TryQuantity(line[(marker + 10)..], out long value)) continue;
            if (line.TrimStart().StartsWith("Total:", StringComparison.Ordinal)) total = value;
            else if (line.Contains("Java Heap", StringComparison.Ordinal)) heap = value;
        }
        return total is { } all && heap is { } managed && all >= managed ? all - managed : null;
    }
    private static bool TryQuantity(string value, out long bytes)
    {
        bytes = 0;
        int end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        if (end == 0 || !long.TryParse(value[..end], NumberStyles.None, CultureInfo.InvariantCulture, out long number)) return false;
        string suffix = value[end..].TrimStart();
        long unit = suffix.StartsWith('G') ? 1024L * 1024 * 1024 : suffix.StartsWith('M') ? 1024L * 1024 : suffix.StartsWith('K') || suffix.StartsWith('k') ? 1024 : 1;
        if (number > long.MaxValue / unit) return false;
        bytes = number * unit;
        return true;
    }
}
