using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Scheduling;

public enum WorkPriority { Critical, Interactive, Background, Idle }
[Flags]
public enum WorkResource { Cpu = 1, Disk = 2, Http = 4 }

public interface IWorkQuietLease : IDisposable
{
    void ReleaseAfter(TimeSpan gracePeriod);
}

/// <summary>Host-owned resource admission. Priority contexts are lexical trusted-code scopes.</summary>
public interface IWorkScheduler
{
    WorkPriority CurrentPriority { get; }
    IDisposable UsePriority(WorkPriority priority);
    ValueTask<IDisposable> AcquireAsync(WorkPriority priority, WorkResource resource, CancellationToken token = default);
    /// <summary>Optional work only: null defers it without creating a waiter.</summary>
    IDisposable? TryAcquire(WorkPriority priority, WorkResource resource, CancellationToken token = default)
    {
        if ((uint)priority > (uint)WorkPriority.Idle) throw new ArgumentOutOfRangeException(nameof(priority));
        if ((uint)resource is 0 or > 7) throw new ArgumentOutOfRangeException(nameof(resource));
        token.ThrowIfCancellationRequested();
        return null;
    }
    IWorkQuietLease EnterQuiet();
}

public sealed record WorkSchedulerOptions(int CpuConcurrency = 2, int DiskConcurrency = 2,
    int HttpConcurrency = 4, int QueueCapacity = 512);
public sealed record WorkQuietSnapshot(bool IsQuiet, int ActiveScopes, long Revision);
public sealed record WorkResourceSnapshot(WorkResource Resource, int Active, int Waiting);
public sealed record WorkSchedulerSnapshot(bool Disposed, int QuietScopes, IReadOnlyList<WorkResourceSnapshot> Resources);

public static class WorkSchedulingContract
{
    public static readonly XsrSemanticId QuietKey = XsrSemanticId.Parse("runtime.work.quiet");
    public static void DeclareState(XsrStateStoreBuilder builder) =>
        builder.Cell<WorkQuietSnapshot>(QuietKey, "Nexa.Services.Scheduling");
}
