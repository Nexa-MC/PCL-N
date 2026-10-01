using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Capabilities;

public static class MachineCapabilityStateContract
{
    public static readonly XsrSemanticId RevisionKey = XsrSemanticId.Parse("machine.capabilities.revision");
    public static readonly XsrSemanticId SnapshotQuery = XsrSemanticId.Parse("machine.capabilities.query");
    public static readonly XsrSemanticId RefreshCommand = XsrSemanticId.Parse("machine.capabilities.refresh");
    public static readonly XsrSemanticId PreflightQuery = XsrSemanticId.Parse("machine.capabilities.preflight.query");
    public static readonly XsrSemanticId RemediationCommand = XsrSemanticId.Parse("machine.capabilities.remediation.execute");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<long>(RevisionKey, "Nexa.Services.Capabilities");
}
public sealed record MachineCapabilityQuery(
    string? InstanceDirectory = null,
    string? InstanceId = null,
    string? MinecraftRootDirectory = null)
{
    public string? JavaExecutablePath { get; init; }
    public long? PlannedHeapMiB { get; init; }
    public int? PlannedClasspathCount { get; init; }
    public int? PlannedRenderDistance { get; init; }
    public string? PlannedLoader { get; init; }
    public bool RefreshInstance { get; init; }
    public bool HasInstanceScope => !string.IsNullOrWhiteSpace(InstanceDirectory) || !string.IsNullOrWhiteSpace(InstanceId);
}
public sealed record MachineCapabilityRefresh;
/// <summary>Evaluates a snapshot already collected for the caller's exact scope. Carrying the
/// immutable snapshot makes the query pure: preflight cannot silently start another hardware or
/// Java probe and cannot advance any capability revision.</summary>
public sealed record LaunchPreflightQuery(MachineCapabilitySnapshot Snapshot);
public interface IMachineCapabilityProvider
{
    string Id { get; }
    ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, MachineCapabilityQuery query,
        CancellationToken cancellationToken) => CollectAsync(timestamp, cancellationToken);
}

/// <summary>Post-collection projection that may publish a coherent family of derived values.</summary>
public interface ICapabilityProjection
{
    IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp);
    IReadOnlyList<ICapability> Project(IReadOnlyDictionary<string, ICapability> values, DateTimeOffset timestamp,
        MachineCapabilityQuery query) => Project(values, timestamp);
}
