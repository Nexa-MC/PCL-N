

namespace Nexa.Services.Minecraft.Process;

/// <summary>Constant memory, whole-run histogram. P95 is an approximate bin upper bound.</summary>
internal sealed class RunResourceHistogram
{
    private readonly long[] _bins = new long[2048];
    private long _count;
    public long Count => _count;
    public long Peak { get; private set; }
    public void Add(long bytes)
    {
        if (bytes < 0) return;
        Peak = Math.Max(Peak, bytes);
        _bins[(int)Math.Min(bytes / (16L * 1024 * 1024), _bins.Length - 1)]++;
        _count++;
    }
    public long P95()
    {
        if (_count == 0) return 0;
        long seen = 0, target = (long)Math.Ceiling(_count * .95);
        for (int i = 0; i < _bins.Length; i++)
        {
            seen += _bins[i];
            if (seen >= target) return Math.Min(Peak, (i + 1L) * 16 * 1024 * 1024 >= 32768L * 1024 * 1024 ? Peak : (i + 1L) * 16 * 1024 * 1024);
        }
        return Peak;
    }
}

internal sealed class JvmRunWindow
{
    private long _count, _cpuCount;
    private double _working, _private, _cpu;
    private long _peak;
    private int _threads;
    public void Add(long working, long privateBytes, double? cpu, int threads)
    {
        _count++; _working += working / 1048576d; _private += privateBytes / 1048576d;
        _peak = Math.Max(_peak, working); _threads = Math.Max(_threads, threads);
        if (cpu is { } measured) { _cpu += measured; _cpuCount++; }
    }
    public JvmRunSample Finish(Guid session, long sequence, long elapsed, long duration, int epoch, JvmRunSettings settings,
        int java, string loader, int classpath, int heap, bool ended, int? exit)
    {
        var sample = new JvmRunSample(session, sequence, elapsed, duration, epoch, settings, java, loader, classpath, heap,
            _count, _count == 0 ? -1 : _working / _count, _count == 0 ? -1 : _peak / 1048576d,
            _count == 0 ? -1 : _private / _count, _cpuCount == 0 ? -1 : _cpu / _cpuCount,
            _count == 0 ? -1 : _threads, ended, exit);
        _count = _cpuCount = _peak = _threads = 0; _working = _private = _cpu = 0;
        return sample;
    }
}
