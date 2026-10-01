

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceRecoverySnapshotSummary(Guid Revision, DateTimeOffset CapturedAt, int Files, long ContentBytes);
public sealed record InstanceRecoveryStorage(long VersionBytes, long SnapshotBytes, bool Complete,
    IReadOnlyList<InstanceRecoverySnapshotSummary> Snapshots, string? Error = null);
