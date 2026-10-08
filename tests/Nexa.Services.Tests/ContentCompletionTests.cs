using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Core.Media;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Resources;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static XsrStateStore ContentCompletionStore()
    {
        var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
        DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder); return builder.Build();
    }

    private static async ValueTask ContentUpdateTransactionPreservesDisabledModsAndReversesWholeBatch()
    {
        foreach (string scenario in new[] { "success", "interrupted", "conflict", "stale", "running" })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = ResourceInstanceFixture(root); var store = ContentCompletionStore();
                List<InstanceContentReplacement> updates = [];
                foreach (string page in new[] { "mods", "resourcepacks" })
                {
                    string directory = Path.Combine(instance, page); Directory.CreateDirectory(directory);
                    string name = page == "mods" ? "old.jar.disabled" : "old.zip", next = page == "mods" ? "new.jar" : "new.zip";
                    string path = Path.Combine(directory, name); await File.WriteAllTextAsync(path, "original-" + page);
                    string stage = Path.Combine(instance, ".nexa-resource-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
                    string staged = Path.Combine(stage, next); await File.WriteAllTextAsync(staged, "updated-" + page);
                    var info = new FileInfo(path);
                    updates.Add(new(new(instance, page, name, false, info.Length, scenario == "stale" && page == "resourcepacks" ? info.LastWriteTimeUtc.Ticks + 1 : info.LastWriteTimeUtc.Ticks), staged, next));
                }
                if (scenario == "running")
                {
                    var process = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 42, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null) { InstanceDirectory = instance };
                    store.PublishDelta(store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [process], []));
                }
                bool failed = false;
                try { await InstanceContentUpdateTransaction.ApplyCoreAsync(updates, store, index => { if (scenario == "interrupted" && index == 1) throw new IOException("injected publish failure"); }); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { failed = true; }
                AssertEqual(scenario is "interrupted" or "stale" or "running", failed);
                string modOriginal = Path.Combine(instance, "mods", "old.jar.disabled"), packOriginal = Path.Combine(instance, "resourcepacks", "old.zip");
                if (failed)
                {
                    AssertEqual("original-mods", await File.ReadAllTextAsync(modOriginal)); AssertEqual("original-resourcepacks", await File.ReadAllTextAsync(packOriginal));
                    AssertFalse(File.Exists(Path.Combine(instance, "mods", "new.jar.disabled")));
                    continue;
                }
                AssertFalse(File.Exists(modOriginal)); AssertFalse(File.Exists(packOriginal));
                AssertEqual("updated-mods", await File.ReadAllTextAsync(Path.Combine(instance, "mods", "new.jar.disabled")));
                AssertFalse(File.Exists(Path.Combine(instance, "mods", "new.jar")));
                var snapshot = await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true });
                var transaction = snapshot.ContentUpdates.Single(); AssertEqual(2, transaction.Items); AssertEqual("committed", transaction.Phase);
                if (scenario == "conflict") await File.WriteAllTextAsync(Path.Combine(instance, "resourcepacks", "new.zip"), "external edit");
                // Reopen the persisted record as a fresh recovery call (no in-memory rollback state).
                var restored = await InstanceContentUpdateTransaction.RollbackAsync(new(instance, transaction.Id), store);
                AssertEqual(scenario != "conflict", restored.IsSuccess);
                if (restored.IsSuccess)
                {
                    AssertEqual("original-mods", await File.ReadAllTextAsync(modOriginal)); AssertEqual("original-resourcepacks", await File.ReadAllTextAsync(packOriginal));
                    AssertFalse(File.Exists(Path.Combine(instance, "mods", "new.jar.disabled")));
                    AssertTrue((await InstanceContentUpdateTransaction.RollbackAsync(new(instance, transaction.Id), store)).IsSuccess);
                }
                else
                {
                    AssertFalse(File.Exists(modOriginal)); AssertEqual("updated-mods", await File.ReadAllTextAsync(Path.Combine(instance, "mods", "new.jar.disabled")));
                    AssertEqual("external edit", await File.ReadAllTextAsync(Path.Combine(instance, "resourcepacks", "new.zip")));
                    AssertTrue(File.Exists(Path.Combine(instance, ".nexa-content-updates", transaction.Id.ToString("N"), "0.old")));
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private sealed class ModCompletionCatalog(byte[] updated) : IResourceCatalogSource, IResourceFileSource
    {
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        private ResourceVersion Version(string id) => new(id, id, id, "正式版", ["1.21.1"], ["fabric"], id == "Old" ? "2025-01-01T00:00:00Z" : "2026-01-01T00:00:00Z", "https://modrinth.com/project/A")
        { ProjectId = "A", File = new("new.jar", "https://cdn.modrinth.com/data/A/new.jar", updated.Length, null, Convert.ToHexString(SHA512.HashData(updated))) };
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(new("A", "Example", "", "", 0, "https://modrinth.com/project/A") { Sources = query.Sources }, "MIT", [Version("New")]));
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token) => Task.FromResult<ResourceVersion?>(Version(command.VersionId));
    }
    private static async ValueTask ModUpdateDownloadsAuthenticatedVersionAndPreservesDisabledState()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"); Directory.CreateDirectory(directory);
            byte[] original = ResourceJar("example"), updated = ResourceJar("example_new");
            string path = Path.Combine(directory, "old.jar.disabled"); await File.WriteAllBytesAsync(path, original); var file = new FileInfo(path);
            string hash = Convert.ToHexString(SHA512.HashData(original)).ToLowerInvariant();
            using var http = new HttpClient(new ResourceHttp(request => request.RequestUri!.Host == "cdn.modrinth.com" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(updated) }
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal)
                    ? new JsonObject { [hash] = new JsonObject { ["project_id"] = "A", ["id"] = "Old" } }.ToJsonString() : "{\"data\":{\"exactMatches\":[]}}")
                }));
            var store = ContentCompletionStore(); var catalog = new ModCompletionCatalog(updated);
            var service = new ResourceContentUpdateService(new(new(new(http, "")), catalog), new(catalog, new(store), new(store), http), store);
            await service.UpdateAsync(new(new(instance, "mods", file.Name, file.Length, file.LastWriteTimeUtc.Ticks), new(ResourceProvider.Modrinth, "A"), "New"), default);
            AssertFalse(File.Exists(path)); AssertTrue((await File.ReadAllBytesAsync(Path.Combine(directory, "new.jar.disabled"))).SequenceEqual(updated));
            AssertEqual(1, (await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true })).ContentUpdates.Count);
            AssertFalse(Directory.EnumerateDirectories(instance, ".nexa-resource-*").Any());
        }
        finally { Directory.Delete(root, true); }
    }

    // Independent wire fixture: unknown tags survive the launcher's DataPacks edit.
    private static byte[] WorldCompletionNbt(int dataVersion = 3955)
    {
        using var body = new MemoryStream();
        void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); Span<byte> size = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)bytes.Length); body.Write(size); body.Write(bytes); }
        void Header(byte type, string name) { body.WriteByte(type); Text(name); }
        void Long(string name, long value) { Header(4, name); Span<byte> data = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(data, value); body.Write(data); }
        void Int(string name, int value) { Header(3, name); Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(data, value); body.Write(data); }
        Header(10, ""); Header(10, "Data"); Header(8, "LevelName"); Text("Fixture 世界"); Int("DataVersion", dataVersion); Long("LastPlayed", 1700000000000);
        Long("RandomSeed", -1234); Int("GameType", 1); Header(1, "Difficulty"); body.WriteByte(2); Header(1, "hardcore"); body.WriteByte(1);
        Header(10, "Version"); Header(8, "Name"); Text("1.21.1"); body.WriteByte(0);
        Header(10, "DataPacks"); Header(9, "Enabled"); body.WriteByte(8); body.Write(new byte[] { 0, 0, 0, 1 }); Text("vanilla");
        Header(9, "Disabled"); body.WriteByte(8); body.Write(new byte[] { 0, 0, 0, 1 }); Text("file/fixture.zip"); body.WriteByte(0);
        Header(8, "PreserveUnknown"); Text("unchanged sentinel"); body.WriteByte(0); body.WriteByte(0);
        using var encoded = new MemoryStream(); using (var gzip = new GZipStream(encoded, CompressionLevel.Optimal, true)) gzip.Write(body.ToArray()); return encoded.ToArray();
    }
    private static async ValueTask WorldMetadataDatapacksCopyAndEditLocksPreserveNbt()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), world = Path.Combine(instance, "saves", "Fixture"); Directory.CreateDirectory(world);
            await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"), WorldCompletionNbt());
            Directory.CreateDirectory(Path.Combine(world, "datapacks")); await File.WriteAllTextAsync(Path.Combine(world, "datapacks", "fixture.zip"), "fixture");
            var store = ContentCompletionStore(); var metadata = await InstanceWorldService.ReadMetadataAsync(world, token: default);
            string legacy = Path.Combine(instance, "saves", "Legacy"); Directory.CreateDirectory(legacy); await File.WriteAllBytesAsync(Path.Combine(legacy, "level.dat"), WorldCompletionNbt(1343));
            var oldMetadata = await InstanceWorldService.ReadMetadataAsync(legacy); AssertFalse(oldMetadata.DataPacksSupported); AssertTrue(metadata.DataPacksSupported);
            AssertFalse((await InstanceWorldService.SetDataPackEnabledAsync(new(instance, "Legacy", "vanilla", true, oldMetadata.Revision), store)).IsSuccess);
            AssertEqual("Fixture 世界", metadata.LevelName); AssertEqual("1.21.1", metadata.GameVersion); AssertEqual<int?>(3955, metadata.DataVersion);
            AssertEqual<long?>(-1234, metadata.Seed); AssertTrue(metadata.Hardcore); AssertTrue(metadata.SizeComplete); AssertTrue(metadata.Size > 0);
            AssertEqual(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), metadata.LastPlayed!.Value);
            AssertFalse((await InstanceWorldService.SetDataPackEnabledAsync(new(instance, "Fixture", "vanilla", false, metadata.Revision), store)).IsSuccess);
            AssertTrue((await InstanceWorldService.SetDataPackEnabledAsync(new(instance, "Fixture", "file/fixture.zip", true, metadata.Revision), store)).IsSuccess);
            metadata = await InstanceWorldService.ReadMetadataAsync(world, token: default); AssertTrue(metadata.DataPacks.Single(item => item.Id == "file/fixture.zip").Enabled);
            using (var input = File.OpenRead(Path.Combine(world, "level.dat")))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var raw = new MemoryStream())
            { await gzip.CopyToAsync(raw); AssertEqual("unchanged sentinel", ServerNbt.Parse(raw.ToArray()).Children.Single(item => item.Name == "Data").String("PreserveUnknown")); }
            AssertTrue(File.Exists(Path.Combine(world, "level.dat.nexa-backup")));
            AssertFalse((await InstanceWorldService.RemoveDataPackAsync(new(instance, "Fixture", "fixture.zip", metadata.Revision), store)).IsSuccess);
            AssertTrue((await InstanceWorldService.SetLockAsync(new(instance, "Fixture", true, metadata.Revision), store)).IsSuccess);
            AssertFalse((await InstanceWorldService.CopyAsync(new(instance, "Fixture", "LockedCopy", metadata.Revision), store)).IsSuccess);
            string sessionPath = Path.Combine(world, "session.lock"); await File.WriteAllTextAsync(sessionPath, "fixture session");
            bool captured = false;
            var backedUp = await InstanceWorldService.BackupAsync(new(instance, "Fixture", metadata.Revision), store, (directory, _) =>
            {
                AssertEqual(world, directory); bool lockHeld = false;
                try { using var blocked = File.OpenRead(sessionPath); } catch (IOException) { lockHeld = true; }
                AssertTrue(lockHeld); captured = true; return Task.CompletedTask;
            }, acquireSessionLock: path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            AssertTrue(backedUp.IsSuccess && captured); using (var released = File.OpenRead(sessionPath)) AssertTrue(released.Length > 0);
            File.Delete(sessionPath);
            AssertTrue((await InstanceWorldService.SetLockAsync(new(instance, "Fixture", false, metadata.Revision), store)).IsSuccess);
            AssertTrue((await InstanceWorldService.CopyAsync(new(instance, "Fixture", "Copy", metadata.Revision), store)).IsSuccess);
            byte[] worldBytes = await File.ReadAllBytesAsync(Path.Combine(world, "level.dat")), copyBytes = await File.ReadAllBytesAsync(Path.Combine(instance, "saves", "Copy", "level.dat"));
            AssertTrue(worldBytes.SequenceEqual(copyBytes));
            AssertFalse((await InstanceWorldService.CopyAsync(new(instance, "Fixture", "Copy", metadata.Revision), store)).IsSuccess);
            AssertFalse((await InstanceWorldService.CopyAsync(new(instance, "Fixture", "../escape", metadata.Revision), store)).IsSuccess);
            AssertTrue((await InstanceWorldService.SetDataPackEnabledAsync(new(instance, "Fixture", "file/fixture.zip", false, metadata.Revision), store)).IsSuccess);
            metadata = await InstanceWorldService.ReadMetadataAsync(world, token: default);
            AssertTrue((await InstanceWorldService.RemoveDataPackAsync(new(instance, "Fixture", "fixture.zip", metadata.Revision), store)).IsSuccess);
            AssertFalse(File.Exists(Path.Combine(world, "datapacks", "fixture.zip"))); AssertTrue(Directory.EnumerateFiles(Path.Combine(world, "datapacks", ".nexa-datapack-trash")).Any());
            metadata = await InstanceWorldService.ReadMetadataAsync(world, token: default);
            var removed = metadata.DataPacks.Single(item => item.Trashed);
            AssertTrue((await InstanceWorldService.RestoreDataPackAsync(new(instance, "Fixture", removed.TrashName, metadata.Revision), store)).IsSuccess);
            AssertTrue(File.Exists(Path.Combine(world, "datapacks", "fixture.zip")));
            string pack = Path.Combine(root, "import.zip");
            using (var archive = ZipFile.Open(pack, ZipArchiveMode.Create))
            { await using var output = archive.CreateEntry("pack.mcmeta").Open(); await output.WriteAsync("{\"pack\":{\"pack_format\":48,\"description\":\"Fixture\"}}"u8.ToArray()); }
            AssertTrue((await InstanceWorldService.ImportDataPackAsync(new(instance, "Fixture", pack, metadata.Revision), store)).IsSuccess);
            AssertTrue(File.Exists(Path.Combine(world, "datapacks", "import.zip")));
            using (var locked = new FileStream(Path.Combine(world, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                AssertFalse((await InstanceWorldService.CopyAsync(new(instance, "Fixture", "InUse", metadata.Revision), store)).IsSuccess);
            var snapshot = await InstanceManagementService.ReadAsync(new(instance)); AssertEqual("Fixture 世界", snapshot.Contents.Single(page => page.PageId == "saves").Entries.Single(item => item.Name == "Fixture").DisplayName);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ContentChangelogRoutesValidateProviderAndVersionIdentity()
    {
        using var http = new HttpClient(new ResourceHttp(request => new(HttpStatusCode.OK)
        { Content = new StringContent("{\"id\":\"New\",\"project_id\":\"A\",\"changelog\":\"Fixed example\\n- change\"}") }));
        var service = new ResourceCatalogService(http);
        AssertEqual("Fixed example\n- change", (await service.ReadChangelogAsync(new(new(ResourceProvider.Modrinth, "A"), "New"), default)).Text);
        bool rejected = false;
        try { await service.ReadChangelogAsync(new(new(ResourceProvider.Modrinth, "B"), "New"), default); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
    }

    private static async ValueTask ScreenshotCropKeepsSourceAndRejectsStaleOrInvalidBounds()
    {
        foreach (string scenario in new[] { "success", "stale", "bounds", "bad-encoding", "cancelled" })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "screenshots"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "test.png");
                byte[] original = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
                await File.WriteAllBytesAsync(path, original); var file = new FileInfo(path);
                var query = new InstanceScreenshotQuery(instance, file.Name, file.Length + (scenario == "stale" ? 1 : 0), file.LastWriteTimeUtc.Ticks);
                int encoded = 0; using var stop = new CancellationTokenSource(); if (scenario == "cancelled") stop.Cancel();
                var result = await InstanceScreenshotService.CropAsync(new(query, scenario == "bounds" ? 1 : 0, 0, 1, 1), ContentCompletionStore(), (bytes, x, y, width, height) =>
                {
                    encoded++; AssertTrue(bytes.Span.SequenceEqual(original)); AssertEqual(1, width); AssertEqual(1, height);
                    return scenario == "bad-encoding" ? "not a png"u8.ToArray() : bytes.ToArray();
                }, stop.Token);
                AssertEqual(scenario == "success", result.IsSuccess); AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(original));
                AssertEqual(scenario is "success" or "bad-encoding" ? 1 : 0, encoded);
                AssertEqual(scenario == "success" ? 2 : 1, Directory.EnumerateFiles(directory).Count());
                AssertFalse(Directory.EnumerateFiles(directory, ".nexa-screenshot-*").Any());
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
