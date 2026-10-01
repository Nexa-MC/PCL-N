using Nexa.Services.Scheduling;

namespace Nexa.Services.Downloads;

/// <summary>Preserves provider capability while admitting each actual connection, including segments.</summary>
internal class ScheduledDownloadConnection(IDownloadConnection inner, IWorkScheduler work,
    WorkPriority priority, WorkResource resources) : IDownloadConnection, IAsyncDisposable
{
    protected IDownloadConnection Inner { get; } = inner;
    private IDisposable? _admission;
    private int _stopped;
    protected async ValueTask AdmitAsync(CancellationToken token) =>
        _admission = await work.AcquireAsync(priority, resources, token).ConfigureAwait(false);
    public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
    {
        await AdmitAsync(cancellationToken).ConfigureAwait(false);
        return await Inner.StartAsync(beginOffset, cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { await Inner.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _admission, null)?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (Inner is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (Inner is IDisposable disposable) disposable.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class ScheduledSegmentedDownloadConnection(ISegmentedDownloadConnection inner, IWorkScheduler work,
    WorkPriority priority, WorkResource resources) : ScheduledDownloadConnection(inner, work, priority, resources), ISegmentedDownloadConnection
{
    public async ValueTask<DownloadConnectionInfo> StartSegmentAsync(long beginOffset, long endOffset, CancellationToken cancellationToken = default)
    {
        await AdmitAsync(cancellationToken).ConfigureAwait(false);
        return await inner.StartSegmentAsync(beginOffset, endOffset, cancellationToken).ConfigureAwait(false);
    }
}
