using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Resources;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstalledBatchReusesPartialIdentityAndRefreshesCaches()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "mods"); Directory.CreateDirectory(directory);
            var queries = new List<ResourceContentOnlineQuery>(); var mapping = new JsonObject();
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(directory, $"mod-{i}.jar"); byte[] bytes = [(byte)i]; await File.WriteAllBytesAsync(path, bytes);
                var file = new FileInfo(path); queries.Add(new(instance, "mods", file.Name, file.Length, file.LastWriteTimeUtc.Ticks));
                mapping[Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant()] = new JsonObject { ["id"] = "A1", ["project_id"] = "A" };
            }
            int modrinth = 0, curseforge = 0; bool partial = true;
            using var http = new HttpClient(new ResourceHttp(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal))
                { Interlocked.Increment(ref modrinth); return new(HttpStatusCode.OK) { Content = new StringContent(mapping.ToJsonString()) }; }
                Interlocked.Increment(ref curseforge);
                if (partial) throw new HttpRequestException("Fixture provider unavailable.");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"exactMatches\":[]}}") };
            }));
            var catalog = new RefreshOnlineCatalog(); var transport = new ResourceProviderHttp(http, "");
            var service = new ResourceContentOnlineService(new(transport), catalog, new ResourceTranslationService(transport));
            var first = await service.ReadBatchAsync(new(queries), default);
            AssertEqual(1, modrinth); AssertEqual(1, curseforge); // No repeated per-file identity lookups on partial failure.
            AssertTrue(first.Matches.All(match => match.Content.Project is not null && match.Content.Notice is { Length: > 0 }));
            AssertTrue(first.Matches.All(match => match.Content.UpdateAvailable == true));
            AssertTrue(catalog.Queries.All(query => !query.Refresh));
            partial = false;
            await service.ReadBatchAsync(new(queries), default); AssertEqual(2, modrinth); AssertEqual(2, curseforge);
            await service.ReadBatchAsync(new(queries), default); AssertEqual(2, modrinth); AssertEqual(2, curseforge);
            catalog.Queries.Clear();
            await service.ReadBatchAsync(new(queries) { Refresh = true }, default);
            AssertEqual(3, modrinth); AssertEqual(3, curseforge); AssertTrue(catalog.Queries.All(query => query.Refresh));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RefreshOnlineCatalog : IResourceCatalogSource
    {
        internal readonly System.Collections.Concurrent.ConcurrentBag<ResourceDetailQuery> Queries = [];
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token)
        {
            Queries.Add(query);
            ResourceVersion Version(string id, string date) => new(id, id, id, "正式版", ["1.21.1"], ["fabric"], date, "")
            { ProjectId = "A", File = new(id + ".jar", "https://cdn.modrinth.com/data/A/" + id + ".jar", 1, new string('a', 40), new string('a', 128)) };
            return Task.FromResult(new ResourceDetail(new("A", "Online", "Description", "", 1, "https://modrinth.com/project/A") { Sources = query.Sources }, "MIT",
                [Version("A1", "2026-01-01T00:00:00Z"), Version("A2", "2026-02-01T00:00:00Z")]));
        }
    }

    private static async ValueTask ExplicitResourceRefreshBypassesMergedDetailCache()
    {
        var source = new RefreshCountingCatalog(); var merged = new MergedResourceCatalog(source, source);
        var query = new ResourceDetailQuery("A");
        await merged.DetailAsync(query, default); await merged.DetailAsync(query, default); AssertEqual(1, source.Reads);
        var updated = await merged.DetailAsync(query with { Refresh = true }, default); AssertEqual(2, source.Reads);
        AssertEqual(updated.Project.Title, (await merged.DetailAsync(query, default)).Project.Title); AssertEqual(2, source.Reads);
    }

    private sealed class RefreshCountingCatalog : IResourceCatalogSource
    {
        internal int Reads;
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(
            new("A", "Revision " + ++Reads, "", "", 1, "https://modrinth.com/project/A"), "MIT", []));
    }
}
