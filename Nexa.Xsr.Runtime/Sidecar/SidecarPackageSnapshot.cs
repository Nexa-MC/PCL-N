namespace Nexa.Xsr.Runtime;

/// <summary>The admitted package's current supervision status, independent of cached content.</summary>
public enum SidecarPackageStatus
{
    Discovered = 1,
    Starting = 2,
    Active = 3,
    Stopped = 4,
    Failed = 5,
    Recovering = 6,
    Quarantined = 7,
    Removed = 8,
}

/// <summary>An immutable, payload-free snapshot of one top-level Sidecar package.</summary>
public sealed record SidecarPackageSnapshot(string PackageName, SidecarPackageStatus Status,
    Guid? SessionId, int? ProcessId, int RecoveryAttempts, string? FailureCode);
