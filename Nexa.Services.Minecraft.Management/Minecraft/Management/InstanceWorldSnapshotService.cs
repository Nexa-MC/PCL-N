using System.Security.Cryptography;
using System.Text;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

/// <summary>World admission stays at the Management boundary; storage receives opaque scopes.</summary>
public sealed class InstanceWorldSnapshotService(IInstanceWorldSnapshotStore snapshots, XsrStateStore store, Func<string, IDisposable>? acquireSessionLock = null)
{
    public async Task<IReadOnlyList<InstanceWorldSnapshot>> ListAsync(InstanceWorldSnapshotListQuery query, CancellationToken token = default)
    {
        var (snapshot, world) = await ResolveAsync(query.InstanceDirectory, query.WorldName, token).ConfigureAwait(false);
        return await snapshots.ListAsync(Scope(snapshot.InstanceDirectory, world), token).ConfigureAwait(false);
    }

    public async Task<InstanceWorldSnapshotVerification> VerifyAsync(InstanceWorldSnapshotVerifyQuery query, CancellationToken token = default)
    {
        var (snapshot, world) = await ResolveAsync(query.InstanceDirectory, query.WorldName, token).ConfigureAwait(false);
        return await snapshots.VerifyAsync(Scope(snapshot.InstanceDirectory, world), query.Identity, token).ConfigureAwait(false);
    }

    public Task<XsrResult> CaptureAsync(InstanceWorldSnapshotCaptureCommand command, CancellationToken token = default) => InstanceWorldService.ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, true,
        async (snapshot, world, _, ct) =>
        {
            await snapshots.CaptureAsync(Scope(snapshot.InstanceDirectory, world), world, command.WorldName, ct).ConfigureAwait(false);
        }, acquireSessionLock, token);

    public Task<XsrResult> RestoreAsync(InstanceWorldSnapshotRestoreCommand command, CancellationToken token = default) => InstanceWorldService.ExecuteAsync(
        command.InstanceDirectory, command.WorldName, command.ExpectedRevision, store, true,
        async (snapshot, world, _, ct) =>
        {
            if (!MinecraftVersionPaths.IsSafeReference(command.DestinationName) || command.DestinationName.StartsWith(".nexa-", StringComparison.Ordinal))
                throw new InvalidDataException("快照副本名称无效。");
            string target = Path.Combine(snapshot.GameDirectory, "saves", command.DestinationName);
            RecoveryBlobStore.CheckLinks(target); if (Path.Exists(target)) throw new IOException("同名世界已存在，未覆盖原世界。");
            string stage = Path.Combine(snapshot.GameDirectory, "saves", ".nexa-world-snapshot-" + Guid.NewGuid().ToString("N"));
            try
            {
                await snapshots.RestoreAsync(Scope(snapshot.InstanceDirectory, world), command.Identity, stage, ct).ConfigureAwait(false);
                await InstanceWorldService.ReadMetadataAsync(stage, token: ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target); Directory.Move(stage, target);
            }
            finally { InstanceWorldService.DeleteOwnedTree(stage); }
        }, acquireSessionLock, token);

    private static async Task<(InstanceManagementSnapshot Snapshot, string World)> ResolveAsync(string instance, string name, CancellationToken token)
    {
        if (!MinecraftVersionPaths.IsSafeReference(name) || name.StartsWith(".nexa-", StringComparison.Ordinal)) throw new InvalidDataException("世界名称无效。");
        var snapshot = await InstanceManagementService.ReadAsync(new(instance), token).ConfigureAwait(false);
        string world = Path.Combine(snapshot.GameDirectory, "saves", name); RecoveryBlobStore.CheckLinks(world);
        if (!Directory.Exists(world)) throw new IOException("世界已不存在。");
        return (snapshot, world);
    }

    private static string Scope(string instance, string world)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance)) + '\0' + Path.TrimEndingDirectorySeparator(Path.GetFullPath(world));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
