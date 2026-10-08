using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Resources;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static byte[] ResourceJar(string id, string? dependency = null, string version = "1.0.0", string? providedId = null, string? additionalDependency = null)
    {
        var dependencies = dependency is null ? new JsonObject() : new JsonObject { [dependency] = ">=1.0.0" };
        if (additionalDependency is not null) dependencies[additionalDependency] = ">=1.0.0";
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open()))
            writer.Write(new JsonObject { ["id"] = id, ["version"] = version, ["depends"] = dependencies, ["provides"] = providedId is null ? null : new JsonArray(JsonValue.Create(providedId)) }.ToJsonString());
        return buffer.ToArray();
    }
    private static string ResourceInstanceFixture(string root) => CreateVersionDirectory(root, "fixture", new JsonObject
    { ["id"] = "fixture", ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient", ["_minecraftVersion"] = "1.21.1", ["libraries"] = new JsonArray((JsonNode)new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.0" }) });

    private static async ValueTask ResourceInstalledContentUsesHashesAndRejectsStaleIdentity()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root);
            byte[] bytes = "a b\r\nc"u8.ToArray(); // Normalized CurseForge payload is "abc", fingerprint 1621425345.
            string sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
            int posts = 0; bool corruptCf = false, unknown = false;
            using var http = new HttpClient(new ResourceHttp(request =>
            {
                posts++;
                string path = request.RequestUri!.AbsolutePath;
                string body = path.EndsWith("version_files", StringComparison.Ordinal)
                    ? unknown ? "{}" : new JsonObject { [sha512] = new JsonObject { ["id"] = "A1", ["project_id"] = "A" } }.ToJsonString()
                    : new JsonObject { ["data"] = new JsonObject { ["exactMatches"] = new JsonArray((JsonNode)new JsonObject { ["file"] = new JsonObject { ["id"] = 99, ["modId"] = 123, ["fileFingerprint"] = 1621425345, ["hashes"] = new JsonArray((JsonNode)new JsonObject { ["algo"] = 1, ["value"] = corruptCf || unknown ? new string('0', 40) : Convert.ToHexString(SHA1.HashData(bytes)) }) } }) } }.ToJsonString();
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            var transport = new ResourceProviderHttp(http, "");
            using var instances = new ResourceInstanceService(transport);
            var catalog = new OnlineCatalog(); var service = new ResourceContentOnlineService(instances, catalog);
            foreach (string page in new[] { "mods", "resourcepacks", "shaderpacks" })
            {
                string directory = Path.Combine(instance, page); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, page == "mods" ? "a.jar.disabled" : "a.zip"); await File.WriteAllBytesAsync(path, bytes);
                var file = new FileInfo(path); var query = new ResourceContentOnlineQuery(instance, page, file.Name, file.Length, file.LastWriteTimeUtc.Ticks);
                var result = await service.ReadAsync(query with { Refresh = true }, default);
                AssertEqual("在线中文名称", result.Project!.DisplayName); AssertEqual("1.0.0", result.InstalledVersion!);
                AssertEqual(page == "mods" ? "fabric" : "", catalog.LastQuery!.Loader);
                AssertEqual("1.21.1", catalog.LastQuery.GameVersion); AssertEqual(2, catalog.LastQuery.Sources.Count);
                corruptCf = true; result = await service.ReadAsync(query with { Refresh = true }, default);
                AssertEqual(1, catalog.LastQuery.Sources.Count); // A Murmur fingerprint alone never proves a match.
                corruptCf = false;
                int before = posts;
                bool rejected = false;
                try { await service.ReadAsync(query with { ExpectedSize = file.Length + 1 }, default); } catch (IOException) { rejected = true; }
                AssertTrue(rejected); AssertEqual(before, posts);
                unknown = true;
                using (var unknownInstances = new ResourceInstanceService(transport))
                {
                    var unknownService = new ResourceContentOnlineService(unknownInstances, catalog);
                    result = await unknownService.ReadAsync(query with { Refresh = true }, default);
                    AssertTrue(result.Project is null && result.Notice is not null);
                }
                unknown = false;
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class OnlineCatalog : IResourceCatalogSource, IResourceFileSource
    {
        internal ResourceDetailQuery? LastQuery;
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
        {
            LastQuery = query;
            return Task.FromResult(new ResourceDetail(new("A", "Name", "Summary", "Author", 12345, "https://modrinth.com/project/A") { ChineseName = "在线中文名称", Sources = query.Sources }, "MIT",
                [new("A1", "Version", "1.0.0", "正式版", ["1.21.1"], ["fabric"], "", "https://modrinth.com/project/A") { ProjectId = "A" }]));
        }
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token) => Task.FromResult<ResourceVersion?>(null);
    }
    private static async ValueTask ResourceModInstallationVerifiesActualDependenciesBeforeImport()
    {
        foreach (string scenario in new[] { "complete", "aliases", "missing", "old" })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = ResourceInstanceFixture(root);
                byte[] a = ResourceJar("a", scenario == "aliases" ? "alias-b" : "b"), b = ResourceJar("b", "a", scenario == "old" ? "0.5.0" : "1.0.0", scenario == "aliases" ? "alias-b" : null);
                ResourceVersion Version(string id, byte[] bytes, params ResourceDependency[] dependencies) => new(id + "1", id, "1.0.0", "正式版", ["1.21.1"], ["fabric"], "", "https://modrinth.com/project/" + id)
                { ProjectId = id, File = new(id + ".jar", "https://cdn.modrinth.com/data/" + id + ".jar", bytes.Length, null, Convert.ToHexString(SHA512.HashData(bytes))), Dependencies = dependencies };
                var versions = scenario == "missing" ? new[] { Version("A", a) } : new[] { Version("A", a, new ResourceDependency("B", "B1", "required")), Version("B", b, new ResourceDependency("A", "A1", "required")) };
                var source = new DependencyCatalog(versions); List<string> order = [];
                using var http = new HttpClient(new ResourceHttp(request =>
                {
                    string path = request.RequestUri!.AbsolutePath;
                    if (path.EndsWith(".jar", StringComparison.Ordinal)) { order.Add(Path.GetFileName(path)); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(path.EndsWith("A.jar", StringComparison.Ordinal) ? a : b) }; }
                    return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("version_files", StringComparison.Ordinal) ? "{}" : "{\"data\":{\"exactMatches\":[]}}") };
                }));
                var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder);
                var store = builder.Build(); var tasks = new TaskCenterService(store); var downloads = new DownloadService(store);
                using var installer = new MinecraftInstallService(tasks, downloads, http: http);
                var service = new ResourceModInstallService(source, new(new(http, "")), new(source, downloads, tasks, http), new(tasks, store, installer), tasks);
                bool failed = false;
                try { await service.InstallAsync(new(new(ResourceProvider.Modrinth, "A"), "A1", new(root, "fixture", false)), default); }
                catch (IOException) { failed = true; }
                bool succeeds = scenario is "complete" or "aliases";
                AssertEqual(!succeeds, failed);
                AssertEqual(scenario == "missing" ? "A.jar" : "B.jar,A.jar", string.Join(',', order));
                AssertEqual(succeeds, File.Exists(Path.Combine(instance, "mods", "A.jar")));
                AssertEqual(succeeds, File.Exists(Path.Combine(instance, "mods", "B.jar")));
                AssertFalse(Directory.EnumerateDirectories(root, ".nexa-resource-*").Any());
                AssertEqual(0, store.Read<TaskCenterSummary>(store.Resolve(TaskCenterStateContract.SummaryKey)).Value!.ActiveCount);
            }
            finally { Directory.Delete(root, true); }
        }
    }
    private static async ValueTask ResourceModRemovalPreservesSharedDependenciesAndRevalidates()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"); Directory.CreateDirectory(directory);
            async Task Write(string id, string? dependency) => await File.WriteAllBytesAsync(Path.Combine(directory, id + ".jar"), ResourceJar(id, dependency));
            await Write("a", "b"); await Write("b", "c"); await Write("c", "b");
            var file = new FileInfo(Path.Combine(directory, "a.jar"));
            var primary = new InstanceContentRemoveCommand(instance, "mods", file.Name, false, file.Length, file.LastWriteTimeUtc.Ticks);
            var preview = await InstanceModRemovalService.PreviewAsync(new(primary)); AssertEqual(2, preview.Orphans.Count);
            await File.WriteAllBytesAsync(Path.Combine(directory, "b.jar"), ResourceJar("b", "c", providedId: "alias-b"));
            preview = await InstanceModRemovalService.PreviewAsync(new(primary)); AssertEqual(2, preview.Orphans.Count);
            await Write("d", "alias-b");
            AssertEqual(0, (await InstanceModRemovalService.PreviewAsync(new(primary))).Orphans.Count);
            var store = new XsrStateStoreBuilder().Build();
            AssertFalse((await InstanceModRemovalService.RemoveAsync(new(primary, preview.Orphans), store)).IsSuccess);
            AssertTrue(File.Exists(file.FullName)); // Stale orphan approval cannot remove a newly shared dependency.
            File.Delete(Path.Combine(directory, "d.jar"));
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown.jar"), "unknown");
            AssertEqual(0, (await InstanceModRemovalService.PreviewAsync(new(primary))).Orphans.Count);
            File.Delete(Path.Combine(directory, "unknown.jar"));
            AssertTrue((await InstanceModRemovalService.RemoveAsync(new(primary, preview.Orphans), store)).IsSuccess);
            AssertEqual(0, Directory.EnumerateFiles(directory).Count());
            var snapshot = await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true }); AssertEqual(3, snapshot.Trash.Count);
            foreach (var item in snapshot.Trash) AssertTrue((await InstanceContentTrash.RestoreAsync(new(instance, item.Id), store)).IsSuccess);
            AssertEqual(3, Directory.EnumerateFiles(directory).Count());
            if (OperatingSystem.IsWindows())
            {
                // Reading stays allowed during preview, but Windows forbids moving the locked prerequisite.
                file.Refresh(); primary = primary with { ExpectedSize = file.Length, ExpectedModifiedUtcTicks = file.LastWriteTimeUtc.Ticks };
                preview = await InstanceModRemovalService.PreviewAsync(new(primary));
                using var locked = new FileStream(Path.Combine(directory, "b.jar"), FileMode.Open, FileAccess.Read, FileShare.Read);
                AssertFalse((await InstanceModRemovalService.RemoveAsync(new(primary, preview.Orphans), store)).IsSuccess);
                AssertFalse(File.Exists(file.FullName));
                AssertTrue(File.Exists(Path.Combine(directory, "b.jar")));
                AssertTrue(File.Exists(Path.Combine(directory, "c.jar")));
                snapshot = await InstanceManagementService.ReadAsync(new(instance) { IncludeTrash = true });
                AssertEqual(1, snapshot.Trash.Count);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ModRemovalImpactIncludesIndirectAliasesAndSkipsDisabledConsumers()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "base.jar"), ResourceJar("base", providedId: "base-alias"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "direct.jar"), ResourceJar("direct", "base-alias"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "indirect.jar"), ResourceJar("indirect", "direct"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "disabled.jar.disabled"), ResourceJar("disabled", "base-alias"));
            var file = new FileInfo(Path.Combine(directory, "base.jar"));
            var command = new InstanceContentRemoveCommand(instance, "mods", file.Name, false, file.Length, file.LastWriteTimeUtc.Ticks);
            var preview = await InstanceModRemovalService.PreviewAsync(new(command));
            AssertEqual(2, preview.RequiredBy.Count);
            AssertTrue(preview.RequiredBy.Any(name => name.Contains("direct", StringComparison.Ordinal)));
            AssertTrue(preview.RequiredBy.Any(name => name.Contains("indirect", StringComparison.Ordinal)));
            AssertFalse(preview.RequiredBy.Any(name => name.Contains("disabled", StringComparison.Ordinal)));
            AssertEqual(0, preview.Orphans.Count);
            // The reverse traversal must terminate when a downstream cycle is present.
            await File.WriteAllBytesAsync(Path.Combine(directory, "direct.jar"), ResourceJar("direct", "base-alias", additionalDependency: "indirect"));
            preview = await InstanceModRemovalService.PreviewAsync(new(command));
            AssertEqual(2, preview.RequiredBy.Count);
            AssertTrue(File.Exists(file.FullName));
        }
        finally { Directory.Delete(root, true); }
    }
    private static ValueTask ResourceProvidedAliasesKeepVersionsAndRejectIncompleteDeclarations()
    {
        var fabric = LaunchModInventoryReader.Parse("""{"id":"provider","version":"1.2.3","provides":["api-alias"]}""", "fabric.mod.json", true).Single();
        AssertTrue(fabric.DependenciesComplete); AssertEqual("1.2.3", fabric.ProvidedIds["api-alias"]);
        var quilt = LaunchModInventoryReader.Parse("""{"quilt_loader":{"id":"provider","version":"1.2.3","provides":["same-version",{"id":"api-alias","version":"2.0.0"}]}}""", "quilt.mod.json", true).Single();
        AssertTrue(quilt.DependenciesComplete); AssertEqual("1.2.3", quilt.ProvidedIds["same-version"]); AssertEqual("2.0.0", quilt.ProvidedIds["api-alias"]);
        var consumer = LaunchModInventoryReader.Parse("""{"id":"consumer","version":"1.0.0","depends":{"api-alias":">=2.0.0"}}""", "fabric.mod.json", true).Single();
        ResourceDependencyVerifier.Verify(new([consumer], 0, true), new([quilt], 0, true));
        bool rejected = false;
        try { ResourceDependencyVerifier.Verify(new([consumer], 0, true), new([fabric], 0, true)); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
        foreach (string declaration in new[] { "{}", "[17]", "[\"bad:id\"]", "[\"alias\",\"alias\"]" })
            AssertFalse(LaunchModInventoryReader.Parse("{\"id\":\"provider\",\"version\":\"1.0.0\",\"provides\":" + declaration + "}", "fabric.mod.json", true).Single().DependenciesComplete);
        return ValueTask.CompletedTask;
    }
}
