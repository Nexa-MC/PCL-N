using Nexa.Services.Composition;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Setup;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask WorldSnapshotHistoryScopesWorldsAndRestoresOnlyVerifiedNewCopies()
    {
        string root = WorkspaceTestDirectory();
        try
        {
            string firstRoot = Path.Combine(root, "one"), secondRoot = Path.Combine(root, "two"); Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(secondRoot);
            string first = ResourceInstanceFixture(firstRoot), second = ResourceInstanceFixture(secondRoot);
            string firstWorld = Path.Combine(first, "saves", "Fixture"), secondWorld = Path.Combine(second, "saves", "Fixture");
            foreach (string world in new[] { firstWorld, secondWorld })
            {
                Directory.CreateDirectory(Path.Combine(world, "region")); await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"), WorldCompletionNbt());
                await File.WriteAllTextAsync(Path.Combine(world, "region", "data.mca"), "immutable world fixture");
            }
            string session = Path.Combine(firstWorld, "session.lock"); await File.WriteAllTextAsync(session, "session sentinel");
            await File.WriteAllTextAsync(Path.Combine(firstWorld, ".nexa-world-readonly"), "locked fixture");
            int leases = 0; var state = ContentCompletionStore(); var backups = new ContentBackupService(Path.Combine(root, "store"));
            var commandsBuilder = new XsrCommandRouterBuilder(); var queriesBuilder = new XsrQueryRouterBuilder(); var observer = new RecordingDispatchObserver();
            WorldSnapshotRuntime.Register(commandsBuilder, queriesBuilder, state, backups, path =>
            { AssertEqual(session, path); leases++; return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            var commands = commandsBuilder.Build(observer); var queries = queriesBuilder.Build(observer);
            AssertTrue(commands.TryResolve(InstanceWorldSnapshotContract.Capture, out var capture));
            AssertTrue(commands.TryResolve(InstanceWorldSnapshotContract.Restore, out var restore));
            AssertTrue(queries.TryResolve(InstanceWorldSnapshotContract.List, out var list));
            AssertTrue(queries.TryResolve(InstanceWorldSnapshotContract.Verify, out var verify));
            string firstRevision = (await InstanceWorldService.ReadMetadataAsync(firstWorld, token: default)).Revision;
            string secondRevision = (await InstanceWorldService.ReadMetadataAsync(secondWorld, token: default)).Revision;
            AssertTrue((await commands.Dispatch(capture, new InstanceWorldSnapshotCaptureCommand(first, "Fixture", firstRevision)).Completion).IsSuccess);
            AssertTrue((await commands.Dispatch(capture, new InstanceWorldSnapshotCaptureCommand(second, "Fixture", secondRevision)).Completion).IsSuccess);
            AssertEqual(1, leases);
            var firstRows = await queries.QueryAsync<InstanceWorldSnapshotListQuery, IReadOnlyList<InstanceWorldSnapshot>>(list, new(first, "Fixture"));
            var secondRows = await queries.QueryAsync<InstanceWorldSnapshotListQuery, IReadOnlyList<InstanceWorldSnapshot>>(list, new(second, "Fixture"));
            AssertTrue(firstRows.IsSuccess && secondRows.IsSuccess); AssertEqual(1, firstRows.Value!.Count); AssertEqual(1, secondRows.Value!.Count);
            AssertFalse(firstRows.Value[0].Identity == secondRows.Value[0].Identity); AssertEqual(2, firstRows.Value[0].Files);
            string identity = firstRows.Value[0].Identity;
            var check = await queries.QueryAsync<InstanceWorldSnapshotVerifyQuery, InstanceWorldSnapshotVerification>(verify, new(first, "Fixture", identity));
            AssertTrue(check.IsSuccess && check.Value!.Ready);
            AssertFalse((await queries.QueryAsync<InstanceWorldSnapshotVerifyQuery, InstanceWorldSnapshotVerification>(verify, new(second, "Fixture", identity))).IsSuccess);
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(second, "Fixture", identity, "CrossScope", secondRevision)).Completion).IsSuccess);
            AssertTrue((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", identity, "Restored", firstRevision)).Completion).IsSuccess);
            string restored = Path.Combine(first, "saves", "Restored");
            AssertEqual("immutable world fixture", await File.ReadAllTextAsync(Path.Combine(restored, "region", "data.mca")));
            AssertEqual("immutable world fixture", await File.ReadAllTextAsync(Path.Combine(firstWorld, "region", "data.mca")));
            AssertFalse(File.Exists(Path.Combine(restored, "session.lock"))); AssertFalse(File.Exists(Path.Combine(restored, ".nexa-world-readonly")));
            AssertEqual("session sentinel", await File.ReadAllTextAsync(session));
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", identity, "Restored", firstRevision)).Completion).IsSuccess);
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", identity, "../escape", firstRevision)).Completion).IsSuccess);
            AssertFalse((await commands.Dispatch(capture, new InstanceWorldSnapshotCaptureCommand(first, "Fixture", "stale")).Completion).IsSuccess);
            var sessions = state.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
            { InstanceDirectory = first, GameDirectory = first };
            state.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertFalse((await commands.Dispatch(capture, new InstanceWorldSnapshotCaptureCommand(first, "Fixture", firstRevision)).Completion).IsSuccess);
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", identity, "RunningCopy", firstRevision)).Completion).IsSuccess);
            state.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1, [], [running.SessionId]));
            var manifest = (await backups.ListAsync()).Single(x => x.Identity == identity);
            AssertFalse(manifest.Label.Contains(root, StringComparison.Ordinal));
            AssertFalse(manifest.Files.Any(x => x.RelativePath is "session.lock" or ".nexa-world-readonly"));
            // Even a byte-valid imported namespace cannot publish invalid world metadata.
            string invalidSource = Path.Combine(root, "invalid-world"); Directory.CreateDirectory(invalidSource);
            await File.WriteAllTextAsync(Path.Combine(invalidSource, "level.dat"), "invalid world metadata");
            var invalid = await backups.CaptureAsync(invalidSource, manifest.Label);
            AssertTrue((await backups.VerifyOfflineAsync(invalid.Identity)).Ready);
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", invalid.Identity, "InvalidMetadataCopy", firstRevision)).Completion).IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(first, "saves", "InvalidMetadataCopy")));
            string corrupt = Path.Combine(root, "store", "objects", manifest.Files.Single(x => x.RelativePath == "region/data.mca").Sha256 + ".blob");
            await File.WriteAllTextAsync(corrupt, "tampered");
            check = await queries.QueryAsync<InstanceWorldSnapshotVerifyQuery, InstanceWorldSnapshotVerification>(verify, new(first, "Fixture", identity));
            AssertTrue(check.IsSuccess); AssertFalse(check.Value!.Ready);
            AssertFalse((await commands.Dispatch(restore, new InstanceWorldSnapshotRestoreCommand(first, "Fixture", identity, "TamperedCopy", firstRevision)).Completion).IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(first, "saves", "TamperedCopy")));
            AssertFalse(Directory.EnumerateDirectories(Path.Combine(first, "saves"), ".nexa-world-snapshot-*").Any());
            using var stop = new CancellationTokenSource(); stop.Cancel();
            AssertFalse((await commands.Dispatch(capture, new InstanceWorldSnapshotCaptureCommand(first, "Fixture", firstRevision), cancellationToken: stop.Token).Completion).IsSuccess);
            AssertEqual(3, (await backups.ListAsync()).Count); AssertTrue(observer.Completed.Count >= 16);
        }
        finally { Directory.Delete(root, true); }
    }
}
