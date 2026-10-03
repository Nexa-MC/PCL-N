using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Resources;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class ChronologyCatalog(ResourceVersion installed, ResourceVersion candidate) : IResourceCatalogSource, IResourceFileSource
    {
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(
            new("A", "Pack", "", "", 0, "https://modrinth.com/project/A") { Sources = query.Sources }, "MIT", [candidate]));
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token) => Task.FromResult<ResourceVersion?>(installed);
    }
    private static async ValueTask InstalledContentUpdateUsesExactIdentityAndKnownChronology()
    {
        foreach (string page in new[] { "mods", "resourcepacks", "shaderpacks" })
            foreach (string scenario in new[] { "newer", "same-number", "older", "unknown", "installed", "same-bytes", "wrong-game", "wrong-loader", "wrong-project" })
            {
                string root = CreateTempDirectory();
                try
                {
                    string instance = ResourceInstanceFixture(root); string directory = Path.Combine(instance, page); Directory.CreateDirectory(directory);
                    byte[] bytes = "installed"u8.ToArray(); string path = Path.Combine(directory, page == "mods" ? "local.jar.disabled" : "local.zip");
                    await File.WriteAllBytesAsync(path, bytes); var file = new FileInfo(path);
                    string hash = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
                    using var http = new HttpClient(new ResourceHttp(request => new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal)
                            ? new JsonObject { [hash] = new JsonObject { ["project_id"] = "A", ["id"] = "Old" } }.ToJsonString()
                            : "{\"data\":{\"exactMatches\":[]}}")
                    }));
                    var old = new ResourceVersion("Old", "Old", "1", "正式版", ["1.21.1"], ["fabric"], scenario == "unknown" ? "" : "2026-01-01T00:00:00Z", "") { ProjectId = "A" };
                    var next = new ResourceVersion(scenario == "installed" ? "Old" : "New", "New", scenario is "same-number" or "installed" ? "1" : "2", "正式版",
                        [scenario == "wrong-game" ? "1.20.1" : "1.21.1"], [scenario == "wrong-loader" ? "forge" : "fabric"],
                        scenario == "older" ? "2025-01-01T00:00:00Z" : "2026-02-01T00:00:00Z", "")
                    { ProjectId = scenario == "wrong-project" ? "B" : "A", File = new("next.zip", "https://cdn.modrinth.com/data/A/next.zip", 10, null, scenario == "same-bytes" ? hash : new string('a', 128)) };
                    var service = new ResourceContentOnlineService(new(new(http, "")), new ChronologyCatalog(old, next));
                    var result = await service.ReadAsync(new(instance, page, file.Name, file.Length, file.LastWriteTimeUtc.Ticks), default);
                    bool updates = scenario is "newer" or "same-number" || scenario == "wrong-loader" && page != "mods";
                    AssertEqual(updates, result.UpdateVersion is not null);
                    AssertEqual(scenario == "unknown" ? (bool?)null : updates, result.UpdateAvailable);
                    AssertEqual("Old", result.InstalledFiles.Single().VersionId);
                    AssertEqual("1", result.InstalledVersion);
                }
                finally { Directory.Delete(root, true); }
            }
    }
    private static async ValueTask ResourceContentBatchAssociatesAllKindsWithBatchedAuthority()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root); byte[] bytes = "resource"u8.ToArray();
            string hash = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(); int posts = 0;
            using var http = new HttpClient(new ResourceHttp(request =>
            {
                Interlocked.Increment(ref posts);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal)
                    ? new JsonObject { [hash] = new JsonObject { ["project_id"] = "A", ["id"] = "A1" } }.ToJsonString() : "{\"data\":{\"exactMatches\":[]}}")
                };
            }));
            List<ResourceContentOnlineQuery> queries = [];
            foreach (string page in new[] { "mods", "resourcepacks", "shaderpacks" })
            {
                string directory = Path.Combine(instance, page); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, page == "mods" ? "different-file-name.jar" : "different-file-name.zip"); await File.WriteAllBytesAsync(path, bytes);
                var file = new FileInfo(path); queries.Add(new(instance, page, file.Name, file.Length, file.LastWriteTimeUtc.Ticks));
            }
            var service = new ResourceContentOnlineService(new(new(http, "")), new OnlineCatalog());
            var result = await service.ReadBatchAsync(new(queries), default);
            AssertEqual(2, posts); AssertEqual(3, result.Matches.Count);
            AssertTrue(result.Matches.All(m => m.Content.Project?.DisplayName == "在线中文名称" && m.Content.InstalledVersion == "1.0.0"));
            await File.WriteAllTextAsync(Path.Combine(instance, "mods", queries[0].Name), "changed");
            result = await service.ReadBatchAsync(new([queries[0]]), default);
            AssertTrue(result.Matches[0].Content.Project is null); AssertEqual(2, posts);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class PackUpdateCatalog(byte[] bytes) : IResourceCatalogSource, IResourceFileSource
    {
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        private ResourceVersion Version(string id) => new(id, id, id == "Old" ? "1.0.0" : "2.0.0", "正式版", ["1.21.1"], [], "", "https://modrinth.com/project/A")
        { ProjectId = "A", File = new("new.zip", "https://cdn.modrinth.com/data/A/new.zip", bytes.Length, null, Convert.ToHexString(SHA512.HashData(bytes))) };
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(
            new("A", "Pack", "", "", 0, "https://modrinth.com/project/A") { Sources = query.Sources }, "MIT", [Version("New")]));
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token) => Task.FromResult<ResourceVersion?>(Version(command.VersionId));
    }
    private static async ValueTask ResourcePackAndShaderUpdatesKeepOriginalOnFailureAndJournalSuccess()
    {
        foreach (string page in new[] { "resourcepacks", "shaderpacks" })
            foreach (string scenario in new[] { "success", "download-error", "stale", "wrong-project", "running", "other-root" })
            {
                string root = CreateTempDirectory();
                try
                {
                    string instance = ResourceInstanceFixture(root); Directory.CreateDirectory(Path.Combine(instance, "mods"));
                    await File.WriteAllBytesAsync(Path.Combine(instance, "mods", "iris.jar"), ResourceJar("iris"));
                    string directory = Path.Combine(instance, page); Directory.CreateDirectory(directory);
                    byte[] original = "old-pack"u8.ToArray(), updated = "new-pack"u8.ToArray(); string path = Path.Combine(directory, "old.zip"); await File.WriteAllBytesAsync(path, original);
                    var old = new FileInfo(path); var query = new ResourceContentOnlineQuery(instance, page, old.Name, old.Length, old.LastWriteTimeUtc.Ticks);
                    string hash = Convert.ToHexString(SHA512.HashData(original)).ToLowerInvariant();
                    using var http = new HttpClient(new ResourceHttp(request => request.RequestUri!.Host == "cdn.modrinth.com"
                        ? new(scenario == "download-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(updated) }
                        : new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal)
                            ? new JsonObject { [hash] = new JsonObject { ["project_id"] = "A", ["id"] = "Old" } }.ToJsonString() : "{\"data\":{\"exactMatches\":[]}}")
                        }));
                    var catalog = new PackUpdateCatalog(updated); var content = new ResourceContentOnlineService(new(new(http, "")), catalog);
                    var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder);
                    MinecraftProcessStateComposition.DeclareState(builder);
                    var store = builder.Build(); var tasks = new TaskCenterService(store); var downloads = new DownloadService(store);
                    if (scenario is "running" or "other-root")
                    {
                        var process = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
                        { InstanceDirectory = scenario == "running" ? instance : Path.Combine(root, "other", "versions", "fixture") };
                        store.PublishDelta(store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [process], []));
                    }
                    var service = new ResourceContentUpdateService(content, new(catalog, downloads, tasks, http), store);
                    bool failed = false;
                    try { await service.UpdateAsync(new(scenario == "stale" ? query with { ExpectedSize = old.Length + 1 } : query, new(ResourceProvider.Modrinth, scenario == "wrong-project" ? "B" : "A"), "New"), default); }
                    catch (Exception error) when (error is IOException or InvalidDataException) { failed = true; }
                    bool succeeds = scenario is "success" or "other-root";
                    AssertEqual(!succeeds, failed);
                    AssertEqual(!succeeds, File.Exists(path));
                    if (succeeds)
                    {
                        AssertTrue((await File.ReadAllBytesAsync(Path.Combine(directory, "new.zip"))).SequenceEqual(updated));
                        var trash = InstanceContentTrash.Read(instance, instance); AssertEqual(1, trash.Count); AssertEqual("old.zip", trash[0].Name);
                    }
                    else AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(original));
                    AssertFalse(Directory.EnumerateDirectories(instance, ".nexa-resource-*").Any());
                }
                finally { Directory.Delete(root, true); }
            }
    }
}
