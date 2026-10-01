namespace Nexa.Services.Downloads;

/// <summary>Bounded file workers and monotonic aggregate progress for independent transfers.</summary>
internal sealed class FileBatchProgress(int count, Action<double, int, long> report)
{
    internal const int Concurrency = 8;
    private readonly object _gate = new();
    private readonly double[] _fractions = new double[count];
    private readonly long[] _speeds = new long[count];
    private int _completed;
    private double _sum, _reported = -1;

    internal void Update(int index, DownloadProgress progress) => Update(index,
        progress.TotalBytes > 0 ? Math.Clamp(progress.DownloadedBytes / (double)progress.TotalBytes, 0, 1) : 0,
        progress.BytesPerSecond, false);

    internal void Complete(int index) => Update(index, 1, 0, true);

    private void Update(int index, double fraction, long speed, bool complete)
    {
        lock (_gate)
        {
            _sum += Math.Max(_fractions[index], fraction) - _fractions[index];
            _fractions[index] = Math.Max(_fractions[index], fraction);
            _speeds[index] = Math.Max(0, speed);
            if (complete) _completed++;
            double overall = count == 0 ? 1 : _sum / count;
            if (!complete && overall - _reported < .002) return;
            _reported = overall;
            report(overall, _completed, _speeds.Sum());
        }
    }

    internal static Task RunAsync(int count, Func<int, CancellationToken, ValueTask> worker, CancellationToken token) =>
        Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions
        { MaxDegreeOfParallelism = Concurrency, CancellationToken = token }, worker);
}
