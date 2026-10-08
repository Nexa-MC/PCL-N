using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceWorldSnapshot(string Identity, DateTimeOffset CreatedAt, int Files, long Bytes);
public sealed record InstanceWorldSnapshotVerification(string Identity, int Files, int VerifiedFiles, IReadOnlyList<string> MissingOrCorruptPaths)
{
    public bool Ready => Files == VerifiedFiles && MissingOrCorruptPaths.Count == 0;
}

public sealed record InstanceWorldSnapshotListQuery(string InstanceDirectory, string WorldName);
public sealed record InstanceWorldSnapshotVerifyQuery(string InstanceDirectory, string WorldName, string Identity);
public sealed record InstanceWorldSnapshotCaptureCommand(string InstanceDirectory, string WorldName, string ExpectedRevision);
public sealed record InstanceWorldSnapshotRestoreCommand(string InstanceDirectory, string WorldName, string Identity, string DestinationName, string ExpectedRevision);

/// <summary>An immutable snapshot store. Scope is opaque and never an absolute path.</summary>
public interface IInstanceWorldSnapshotStore
{
    Task<IReadOnlyList<InstanceWorldSnapshot>> ListAsync(string scope, CancellationToken token);
    Task<InstanceWorldSnapshot> CaptureAsync(string scope, string worldDirectory, string displayName, CancellationToken token);
    Task<InstanceWorldSnapshotVerification> VerifyAsync(string scope, string identity, CancellationToken token);
    Task RestoreAsync(string scope, string identity, string destination, CancellationToken token);
}

public static class InstanceWorldSnapshotContract
{
    public static readonly XsrSemanticId List = XsrSemanticId.Parse("minecraft.world.snapshots.list");
    public static readonly XsrSemanticId Verify = XsrSemanticId.Parse("minecraft.world.snapshots.verify");
    public static readonly XsrSemanticId Capture = XsrSemanticId.Parse("minecraft.world.snapshots.capture");
    public static readonly XsrSemanticId Restore = XsrSemanticId.Parse("minecraft.world.snapshots.restore-new");
}
