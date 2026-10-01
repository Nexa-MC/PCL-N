using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>
/// Writes launch stage reports into the shared host store. Each report publishes the whole
/// cell set so readers always see one coherent stage snapshot; publishing never throws into
/// the launch pipeline.
/// </summary>
public class MinecraftLaunchProgressPublisher(XsrStateStore store)
{
    private readonly XsrStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly object _gate = new();
    private readonly XsrStateId _snapshotId = store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
    private readonly XsrStateId _acquirePendingId = store.Resolve(MinecraftLaunchProgressState.AcquirePendingKey);
    private readonly XsrStateId _acquireComponentId = store.Resolve(MinecraftLaunchProgressState.AcquireComponentKey);
    private readonly XsrStateId _acquireMajorId = store.Resolve(MinecraftLaunchProgressState.AcquireMajorKey);
    private MinecraftLaunchProgressSnapshot _current = MinecraftLaunchProgressSnapshot.Empty;

    private string? _instanceId, _root;
    public void Start(string instanceId, string root)
    {
        lock (_gate) { _instanceId = instanceId; _root = Path.GetFullPath(root); Start(); }
    }

    public void Start() => Publish(new MinecraftLaunchStageReport(
        MinecraftLaunchStages.GetJava, 0d, IsLaunched: false, Method: null, DownloadSpeed: null));

    public virtual void Report(MinecraftLaunchStageReport report)
    {
        Publish(report);
    }

    /// <summary>Marks a Java runtime acquisition as awaiting the user's decision.</summary>
    public void RequestAcquisition(string component, int majorVersion, IReadOnlyList<int>? choices = null)
    {
        try
        {
            // Publish payload before the ready flag so a UI observer never opens a decision
            // surface with a stale component or Java major from an earlier acquisition.
            _store.Publish(_store.Resolve(MinecraftLaunchProgressState.JavaChoicesKey), choices ?? Array.AsReadOnly(new[] { majorVersion }));
            _store.Publish(_acquireComponentId, component);
            _store.Publish(_acquireMajorId, majorVersion);
            _store.Publish(_acquirePendingId, true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline.
        }
    }

    /// <summary>Clears the acquisition prompt after a decision (or cancellation).</summary>
    public void ResolveAcquisition()
    {
        try
        {
            _store.Publish(_acquirePendingId, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
        }
    }

    public void Stop()
    {
        PublishSnapshot(MinecraftLaunchProgressSnapshot.Empty);
    }

    /// <summary>
    /// Resets progress only when <paramref name="sessionId"/> is still the session represented by
    /// the current launch. Retaining the terminal ID lets Desktop correlate the process roster
    /// without leaving active/stage/launched facts stale.
    /// </summary>
    public bool Stop(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            return false;
        }

        lock (_gate)
        {
            if (_current.SessionId != sessionId)
            {
                return false;
            }

            PublishSnapshotLocked(MinecraftLaunchProgressSnapshot.Empty with { SessionId = sessionId });
            return true;
        }
    }

    private void Publish(MinecraftLaunchStageReport report)
    {
        if (string.IsNullOrEmpty(report.Stage))
        {
            return;
        }

        PublishSnapshot(new MinecraftLaunchProgressSnapshot(
            true,
            report.Stage,
            Math.Clamp(report.Progress, 0d, 1d),
            report.Method ?? string.Empty,
            report.DownloadSpeed ?? string.Empty,
            report.IsLaunched,
            report.SessionId)
        { InstanceId = _instanceId, MinecraftRootDirectory = _root });
    }

    private void PublishSnapshot(MinecraftLaunchProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            PublishSnapshotLocked(snapshot);
        }
    }

    private void PublishSnapshotLocked(MinecraftLaunchProgressSnapshot snapshot)
    {
        try
        {
            _store.Publish(_snapshotId, snapshot);
            _current = snapshot;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Progress publication must never break the launch pipeline. Keep the in-memory
            // value aligned with the last successfully published snapshot.
        }
    }
}
