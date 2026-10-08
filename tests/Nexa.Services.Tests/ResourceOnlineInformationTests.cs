using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Caching;
using Nexa.Services.Downloads;
using Nexa.Services.Resources;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class DurableResourceCatalog : IResourceCatalogSource, IResourceFileSource
    {
        internal int Calls;
        internal bool Offline;
        internal TaskCompletionSource? Block;
        internal byte[] Payload = "durable-file"u8.ToArray();
        public async Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Block is { } block) await block.Task.WaitAsync(token);
            if (Offline) throw new HttpRequestException();
            return new([new("Project", "Cached title", "Description", "Author", 1, "https://modrinth.com/project/Project")
            { Sources = [new(ResourceProvider.Modrinth, "Project")], IconUrl = "https://cdn.modrinth.com/icon.png?credential=private" }], 1, query.Page);
        }
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Offline) throw new HttpRequestException();
            return Task.FromResult(new ResourceDetail(new(query.ProjectId, "Cached title", "Description", "Author", 1, "https://modrinth.com/project/Project")
            { Sources = [new(ResourceProvider.Modrinth, query.ProjectId)] }, "MIT", [Version(query.ProjectId, "Version")]));
        }
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Offline) throw new HttpRequestException();
            return Task.FromResult<ResourceVersion?>(Version(command.ProjectId, command.VersionId));
        }
        private ResourceVersion Version(string project, string version) => new(version, "Cached version", "1", "正式版", ["1.21.1"], ["fabric"], "2026-01-01", "https://modrinth.com/project/Project")
        { ProjectId = project, File = new("file.jar", "https://cdn.modrinth.com/file.jar?credential=private", Payload.Length, null, Convert.ToHexString(SHA512.HashData(Payload))) };
    }

    private static async ValueTask ResourceOnlineInformationSurvivesRestartAndOfflineRefresh()
    {
        string directory = CreateTempDirectory();
        try
        {
            var provider = new DurableResourceCatalog();
            var search = new ResourceSearchQuery(Text: "durable-metadata");
            var detail = new ResourceDetailQuery("Project", "1.21.1", "fabric");
            var file = new ResourceDownloadCommand(ResourceProvider.Modrinth, "Project", "Version", "");
            using (var state = new SharedStateCache())
            using (var information = new ResourceOnlineInformationCache(state, directory))
            using (var catalog = new MergedResourceCatalog(provider, provider, information, () => "policy-one"))
            {
                await catalog.SearchAsync(search, default);
                await catalog.DetailAsync(detail, default);
                await catalog.ReadVersionAsync(file, default);
            }
            AssertEqual(3, Directory.EnumerateFiles(directory, "*.json").Count());
            AssertFalse(Directory.EnumerateFiles(directory).Any(path => File.ReadAllText(path).Contains("credential=private", StringComparison.Ordinal)));
            int calls = provider.Calls; provider.Offline = true;
            using (var state = new SharedStateCache())
            using (var information = new ResourceOnlineInformationCache(state, directory))
            using (var catalog = new MergedResourceCatalog(provider, provider, information, () => "policy-one"))
            {
                AssertEqual("Cached title", (await catalog.SearchAsync(search, default)).Projects[0].Title);
                AssertTrue((await catalog.SearchAsync(search, default)).Projects[0].IconUrl is null);
                AssertEqual("Version", (await catalog.DetailAsync(detail, default)).Versions[0].Id);
                AssertEqual("", (await catalog.ReadVersionAsync(file, default))!.File!.Url);
                AssertEqual(calls, provider.Calls);
                var retained = await catalog.DetailAsync(detail with { Refresh = true }, default);
                AssertTrue(retained.Notice!.Contains("缓存资料", StringComparison.Ordinal));
                AssertEqual(calls + 1, provider.Calls);
            }
            // A persisted version with its link omitted cannot strand a live download behind the fresh cache.
            provider.Offline = false;
            using (var information = new ResourceOnlineInformationCache(directory: directory))
            using (var catalog = new MergedResourceCatalog(provider, provider, information, () => "policy-one"))
            using (var http = new HttpClient(new ResourceHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(provider.Payload) })))
            {
                string destination = Path.Combine(directory, "downloads"); Directory.CreateDirectory(destination);
                var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder);
                var store = builder.Build();
                int before = provider.Calls;
                await new ResourceDownloadService(catalog, new(store), new(store), http).DownloadAsync(file with { DestinationDirectory = destination, MirrorFirst = false }, default);
                AssertEqual(before + 1, provider.Calls);
                AssertTrue((await File.ReadAllBytesAsync(Path.Combine(destination, "file.jar"))).SequenceEqual(provider.Payload));
            }
            provider.Offline = true;
            // Original timestamps survive import: old metadata is immediately available while one refresh runs.
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                document["StoredAt"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
                await File.WriteAllTextAsync(path, document.ToJsonString());
            }
            using (var state = new SharedStateCache())
            using (var information = new ResourceOnlineInformationCache(state, directory))
            using (var catalog = new MergedResourceCatalog(provider, provider, information, () => "policy-one"))
            {
                provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var retained = await catalog.SearchAsync(search, default);
                AssertTrue(retained.Notice!.Contains("缓存资料", StringComparison.Ordinal));
                var retainedAgain = await catalog.SearchAsync(search, default);
                AssertEqual("Cached title", retainedAgain.Projects[0].Title);
                provider.Block.TrySetResult();
            }
            provider.Block = null;
            using (var state = new SharedStateCache())
            using (var information = new ResourceOnlineInformationCache(state, directory))
            using (var catalog = new MergedResourceCatalog(provider, provider, information, () => "policy-two"))
            {
                bool failed = false;
                try { await catalog.SearchAsync(search, default); } catch (IOException) { failed = true; }
                AssertTrue(failed); // A changed provider policy cannot masquerade as the old policy.
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask ResourceOnlineInformationCoalescesConcurrentCallersAndRejectsCorruption()
    {
        string directory = CreateTempDirectory();
        try
        {
            var provider = new DurableResourceCatalog { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var information = new ResourceOnlineInformationCache(directory: directory);
            using var catalog = new MergedResourceCatalog(provider, provider, information);
            var query = new ResourceSearchQuery(Text: "coalesced-metadata");
            using var stop = new CancellationTokenSource();
            Task<ResourceSearchResult> canceled = catalog.SearchAsync(query, stop.Token), surviving = catalog.SearchAsync(query, default);
            stop.Cancel();
            bool propagated = false;
            try { await canceled; } catch (OperationCanceledException) { propagated = true; }
            AssertTrue(propagated);
            provider.Block.TrySetResult();
            AssertEqual("Cached title", (await surviving).Projects[0].Title);
            AssertEqual(2, provider.Calls); // One request per provider, despite two page consumers.
            await catalog.SearchAsync(query, default); AssertEqual(2, provider.Calls);
            await catalog.SearchAsync(query with { WaitForRefresh = true }, default); AssertEqual(2, provider.Calls);
            // A UI renewal that arrives after the background owner finished consumes the fresh result.

            string path = Directory.EnumerateFiles(directory, "*.json").Single();
            var record = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            record["Identity"] = "other-file";
            await File.WriteAllTextAsync(path, record.ToJsonString());
            using var restarted = new ResourceOnlineInformationCache(directory: directory);
            using var restartedCatalog = new MergedResourceCatalog(provider, provider, restarted);
            await restartedCatalog.SearchAsync(query, default); AssertEqual(4, provider.Calls);
            provider.Offline = true;
            using var different = new ResourceOnlineInformationCache(directory: directory);
            using var differentCatalog = new MergedResourceCatalog(provider, provider, different);
            bool wrong = false;
            try { await differentCatalog.SearchAsync(query with { GameVersion = "1.20.1" }, default); } catch (IOException) { wrong = true; }
            AssertTrue(wrong);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask ResourceExactIdentityAndTranslationsReuseAfterRestart()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"), cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(directory);
            byte[] bytes = "disabled-fixture"u8.ToArray(); string path = Path.Combine(directory, "fixture.disabled.jar");
            await File.WriteAllBytesAsync(path, bytes);
            string hash = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant(); int posts = 0, translations = 0;
            using var http = new HttpClient(new ResourceHttp(request =>
            {
                string body;
                if (request.RequestUri!.AbsolutePath.Contains("/translate/", StringComparison.Ordinal))
                { translations++; body = "{\"original\":\"Description\",\"translated\":\"中文资料\"}"; }
                else
                {
                    posts++;
                    body = request.RequestUri.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal)
                        ? new JsonObject { [hash] = new JsonObject { ["project_id"] = "Project", ["id"] = "Version" } }.ToJsonString() : "{\"data\":{\"exactMatches\":[]}}";
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            var transport = new ResourceProviderHttp(http, "") { CountryPolicy = new("CN") };
            using (var information = new ResourceOnlineInformationCache(directory: cache))
            using (var instances = new ResourceInstanceService(transport, information))
            using (var translated = new ResourceTranslationService(transport, information))
            {
                var context = await instances.ReadAsync(new(root, "fixture"), default);
                AssertFalse(context.Installed.Single().Enabled);
                AssertEqual("中文资料", (await translated.ReadAsync(new(new(ResourceProvider.Modrinth, "Project"), "Description"), default)).Description!);
            }
            using (var information = new ResourceOnlineInformationCache(directory: cache))
            using (var instances = new ResourceInstanceService(transport, information))
            using (var translated = new ResourceTranslationService(transport, information))
            {
                File.Move(path, Path.Combine(directory, "renamed.jar"));
                var context = await instances.ReadAsync(new(root, "fixture"), default);
                AssertEqual("renamed.jar", context.Installed.Single().FileName); AssertTrue(context.Installed[0].Enabled);
                AssertEqual(2, posts);
                AssertEqual("中文资料", (await translated.ReadAsync(new(new(ResourceProvider.Modrinth, "Project"), "Description"), default)).Description!);
                AssertEqual(1, translations);
                AssertTrue((await translated.ReadAsync(new(new(ResourceProvider.Modrinth, "Project"), "Changed"), default)).Description is null);
                AssertEqual(2, translations);
                AssertTrue((await translated.ReadAsync(new(new(ResourceProvider.Modrinth, "Project"), "Changed"), default)).Description is null);
                AssertEqual(3, translations); // Mismatched/negative translations do not poison later lookups.
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static async ValueTask ResourceOnlineInformationOwnsDiskPathsAndFreezesSnapshots()
    {
        string directory = CreateTempDirectory(), outside = CreateTempDirectory();
        try
        {
            string cache = Path.Combine(directory, "cache");
            var provider = new DurableResourceCatalog(); var query = new ResourceSearchQuery(Text: "owned-metadata");
            using (var information = new ResourceOnlineInformationCache(directory: cache))
            using (var catalog = new MergedResourceCatalog(provider, provider, information))
            {
                var search = await catalog.SearchAsync(query, default);
                RejectMutation(search.Projects, search.Projects[0] with { Title = "Poisoned" });
                RejectMutation(search.Projects[0].Sources, new(ResourceProvider.Modrinth, "Poisoned"));
                var detail = await catalog.DetailAsync(new("Project"), default);
                RejectMutation(detail.Versions, detail.Versions[0] with { Number = "Poisoned" });
                RejectMutation(detail.Versions[0].Games, "Poisoned");
                RejectMutation(detail.Versions[0].Loaders, "Poisoned");
                RejectMutation(detail.Versions[0].Dependencies, new("Poisoned", null, "required"));
                AssertEqual("Cached title", (await catalog.SearchAsync(query, default)).Projects[0].Title);
                AssertTrue(detail.Versions[0].File!.Url.Contains("credential=private", StringComparison.Ordinal));
            }
            using (var information = new ResourceOnlineInformationCache(directory: cache))
            using (var catalog = new MergedResourceCatalog(provider, provider, information))
            {
                var imported = await catalog.DetailAsync(new("Project"), default);
                RejectMutation(imported.Project.Sources, new(ResourceProvider.Modrinth, "Poisoned"));
                RejectMutation(imported.Versions, imported.Versions[0] with { Number = "Poisoned" });
                RejectMutation(imported.Versions[0].Games, "Poisoned");
                RejectMutation(imported.Versions[0].Loaders, "Poisoned");
                RejectMutation(imported.Versions[0].Dependencies, new("Poisoned", null, "required"));
                AssertEqual("", imported.Versions[0].File!.Url);
            }
            if (!OperatingSystem.IsWindows())
            {
                string externalCache = Path.Combine(outside, "cache"); Directory.CreateDirectory(externalCache);
                foreach (string path in Directory.EnumerateFiles(cache, "*.json")) File.Copy(path, Path.Combine(externalCache, Path.GetFileName(path)));
                string alias = Path.Combine(directory, "alias"); Directory.CreateSymbolicLink(alias, outside);
                int existing = Directory.EnumerateFiles(externalCache).Count(); provider.Offline = true;
                using (var information = new ResourceOnlineInformationCache(directory: Path.Combine(alias, "cache")))
                using (var catalog = new MergedResourceCatalog(provider, provider, information))
                {
                    bool missed = false;
                    try { await catalog.SearchAsync(query, default); } catch (IOException) { missed = true; }
                    AssertTrue(missed); // A valid record behind a linked ancestor must not be imported.
                }
                provider.Offline = false;
                using (var information = new ResourceOnlineInformationCache(directory: Path.Combine(alias, "cache")))
                using (var catalog = new MergedResourceCatalog(provider, provider, information))
                    await catalog.SearchAsync(query with { Text = "must-not-write-through-link" }, default);
                AssertEqual(existing, Directory.EnumerateFiles(externalCache).Count());
                using (var information = new ResourceOnlineInformationCache(directory: Path.Combine(alias, "new-cache")))
                using (var catalog = new MergedResourceCatalog(provider, provider, information))
                    await catalog.SearchAsync(query with { Text = "must-not-create-through-link" }, default);
                AssertFalse(Directory.Exists(Path.Combine(outside, "new-cache")));
                Directory.Delete(alias);
            }
            string note = Path.Combine(cache, "notes.json"); const string userContent = "{\"user\":\"retain\"}";
            await File.WriteAllTextAsync(note, userContent); File.SetLastWriteTimeUtc(note, DateTime.UtcNow.AddYears(-1));
            for (int index = 0; index < 257; index++)
            {
                string path = Path.Combine(cache, index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture) + ".json");
                await File.WriteAllTextAsync(path, "{}"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            }
            using (var first = new ResourceOnlineInformationCache(directory: cache))
            using (var second = new ResourceOnlineInformationCache(directory: cache))
            using (var firstCatalog = new MergedResourceCatalog(provider, provider, first))
            using (var secondCatalog = new MergedResourceCatalog(provider, provider, second))
                await Task.WhenAll(firstCatalog.SearchAsync(query with { Text = "budget-first" }, default),
                    secondCatalog.SearchAsync(query with { Text = "budget-second" }, default));
            AssertEqual(userContent, await File.ReadAllTextAsync(note));
            AssertTrue(Directory.EnumerateFiles(cache, "*.json").Count(path => Path.GetFileNameWithoutExtension(path).Length == 64) <= 256);
            AssertEqual(0, Directory.EnumerateFiles(cache, "*.tmp").Count());
        }
        finally { Directory.Delete(directory, true); Directory.Delete(outside, true); }

        static void RejectMutation<T>(IReadOnlyList<T> values, T replacement)
        {
            if (values is not IList<T> writable) return;
            bool rejected = false;
            try { if (writable.Count == 0) writable.Add(replacement); else writable[0] = replacement; }
            catch (NotSupportedException) { rejected = true; }
            AssertTrue(rejected);
        }
    }

}
