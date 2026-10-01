using System.Globalization;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Persisted options, not a claim that the running game applied them.</summary>
public sealed record JvmRunSettings(int RenderDistance = -1, int SimulationDistance = -1,
    int MaxFps = -1, int Graphics = -1, int Vsync = -1, int Fullscreen = -1)
{
    public static JvmRunSettings Read(string directory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(directory, "options.txt"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Bound actual bytes, including a file growing while it is read.
            byte[] bytes = new byte[65537];
            int length = 0, read;
            while (length < bytes.Length && (read = stream.Read(bytes.AsSpan(length))) > 0) length += read;
            return length > 65536 ? new() : Parse(System.Text.Encoding.UTF8.GetString(bytes, 0, length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }
    internal static JvmRunSettings Parse(string text)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 1) continue;
            string key = line[..separator];
            if (key is not ("renderDistance" or "simulationDistance" or "maxFps" or "graphicsMode" or "enableVsync" or "fullscreen")) continue;
            string value = line[(separator + 1)..].Trim();
            int maximum = key is "enableVsync" or "fullscreen" ? 1 : key == "graphicsMode" ? 2 : key == "maxFps" ? 10000 : 256;
            int number = value == "true" ? 1 : value == "false" ? 0 : int.TryParse(value, CultureInfo.InvariantCulture, out int n) ? n : -1;
            values[key] = number >= 0 && number <= maximum ? number : -1;
        }
        return new(values.GetValueOrDefault("renderDistance", -1), values.GetValueOrDefault("simulationDistance", -1),
            values.GetValueOrDefault("maxFps", -1), values.GetValueOrDefault("graphicsMode", -1),
            values.GetValueOrDefault("enableVsync", -1), values.GetValueOrDefault("fullscreen", -1));
    }
}

public sealed record JvmRunSample(Guid SessionId, long Sequence, long ElapsedMilliseconds, long WindowMilliseconds,
    int ConfigurationEpoch, JvmRunSettings Settings, int JavaMajor, string Loader, int ClasspathCount,
    int HeapLimitMiB, long SampleCount, double WorkingMeanMiB, double WorkingPeakMiB,
    double PrivateMeanMiB, double CpuMeanPercent, int ThreadsPeak, bool Ended, int? ExitCode)
{
    public string Key => $"{SessionId:N}:{Sequence}";
}
