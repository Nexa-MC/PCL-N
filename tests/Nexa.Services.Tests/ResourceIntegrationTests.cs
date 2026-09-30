using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nexa.Services.Downloads;
using Nexa.Services.Resources;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ResourceDualSourceChineseIdentityAndPartialFailure()
    {
        List<string> terms = [];
        var mr = new CatalogStub((query, _) =>
        {
            lock (terms) terms.Add(query.Text); return Task.FromResult(new ResourceSearchResult([
            new("Sodium", "Sodium", "Renderer", "", 1, "https://modrinth.com/project/Sodium") { Slug = "sodium", Sources = [new(ResourceProvider.Modrinth, "Sodium")] }], 1, 0));
        });
        var cf = new CatalogStub((query, _) =>
        {
            lock (terms) terms.Add(query.Text); return Task.FromResult(new ResourceSearchResult([
            new("394468", "Sodium", "Renderer", "", 1, "https://www.curseforge.com/projects/394468") { Slug = "sodium", Sources = [new(ResourceProvider.CurseForge, "394468")] }], 1, 0));
        });
        var merged = new MergedResourceCatalog(mr, cf);
        var result = await merged.SearchAsync(new(Text: "钠"), default);
        AssertEqual(1, result.Projects.Count); AssertEqual(2, result.Projects[0].Sources.Count);
        AssertTrue(result.Projects[0].DisplayName.Contains('钠')); AssertTrue(terms.Contains("sodium") && terms.All(term => !term.Contains('钠')));
        var broken = new CatalogStub((_, _) => throw new HttpRequestException());
        result = await new MergedResourceCatalog(mr, broken).SearchAsync(new(), default);
        AssertEqual(1, result.Projects.Count); AssertTrue(result.Notice!.Contains("CurseForge", StringComparison.Ordinal));
        var unrelated = new CatalogStub((_, _) => Task.FromResult(new ResourceSearchResult([
            new("Other", "Sodium", "", "", 1, "https://www.curseforge.com/projects/2") { Slug = "different", Sources = [new(ResourceProvider.CurseForge, "2")] }], 1, 0)));
        result = await new MergedResourceCatalog(mr, unrelated).SearchAsync(new(), default);
        AssertEqual(2, result.Projects.Count); // Matching display names alone cannot establish identity.
    }
    private static async ValueTask ResourceMirrorFallbackDoesNotLeakCredentials()
    {
        List<string> hosts = [];
        using var http = new HttpClient(new ResourceHttp(request =>
        {
            string host = request.RequestUri!.Host; hosts.Add(host);
            AssertEqual(host == "api.curseforge.com", request.Headers.Contains("x-api-key"));
            return new(host == "mod.mcimirror.top" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        using var doc = await new ResourceProviderHttp(http, "test-key").ReadAsync(ResourceProvider.CurseForge, "mods/1", true, default);
        AssertEqual("mod.mcimirror.top", hosts[0]); AssertEqual("api.curseforge.com", hosts[1]);
    }
    private static async ValueTask ResourceTranslationRequiresMatchingOriginal()
    {
        int calls = 0;
        using var http = new HttpClient(new ResourceHttp(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("""{"original":"Renderer","translated":"渲染优化"}""") }; }));
        var translations = new ResourceTranslationService(new(http));
        var query = new ResourceTranslationQuery(new(ResourceProvider.Modrinth, "Sodium"), "Renderer");
        AssertEqual("渲染优化", (await translations.ReadAsync(query, default)).Description!);
        AssertEqual("渲染优化", (await translations.ReadAsync(query, default)).Description!); AssertEqual(1, calls);
        AssertTrue((await translations.ReadAsync(query with { Original = "Changed" }, default)).Description is null);
    }
    private static async ValueTask ResourceCurseForgeMapsFiltersFilesAndDeniedDownloads()
    {
        List<string> requests = [];
        using var http = new HttpClient(new ResourceHttp(request =>
        {
            requests.Add(Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri));
            const string project = """{"id":123,"name":"Test","slug":"test","summary":"Description","authors":[{"name":"Author"}],"downloadCount":12,"logo":{"thumbnailUrl":"https://media.forgecdn.net/avatars/1/2.png"}}""";
            string body = request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) ? "{\"data\":[" + project + "],\"pagination\":{\"totalCount\":30}}"
                : request.RequestUri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal) ? """{"data":[{"id":1,"modId":123,"displayName":"One","fileName":"one.jar","fileLength":3,"downloadUrl":"https://edge.forgecdn.net/files/one.jar","isAvailable":true,"releaseType":1,"gameVersions":["1.21.1","Fabric"],"hashes":[{"algo":1,"value":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]},{"id":2,"modId":123,"displayName":"Denied","fileName":"two.jar","gameVersions":["1.21.1","Fabric"],"downloadUrl":null},{"id":3,"modId":999,"gameVersions":["1.21.1","Fabric"]}]}"""
                : "{\"data\":" + project + "}";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var source = new CurseForgeResourceCatalog(new(http, ""));
        var result = await source.SearchAsync(new(ResourceKind.Mod, "test", "1.21.1", "fabric", Page: 1), default);
        AssertEqual(30, result.Total); AssertTrue(result.Projects[0].IconUrl is not null);
        AssertTrue(requests[0].Contains("modLoaderType=4", StringComparison.Ordinal)); AssertTrue(requests[0].Contains("index=20", StringComparison.Ordinal));
        var detail = await source.DetailAsync(new("123", "1.21.1", "fabric"), default);
        AssertEqual(2, detail.Versions.Count); AssertTrue(detail.Versions[0].File is not null); AssertTrue(detail.Versions[1].File is null);
    }
    private static async ValueTask ResourceDownloadsValidateActualBytesHashAndNeverOverwrite()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes("valid archive payload");
            string hash = Convert.ToHexString(SHA512.HashData(data));
            foreach (string scenario in new[] { "success", "hash", "extra", "short", "overwrite", "redirect", "cancel" })
            {
                string name = scenario + ".jar";
                if (scenario == "overwrite") await File.WriteAllTextAsync(Path.Combine(directory, name), "keep");
                var file = new ResourceFile(name, "https://cdn.modrinth.com/data/test/file.jar", data.Length, null, hash);
                var catalog = new CatalogStub((_, _) => throw new NotSupportedException()) { File = file };
                using var cancel = new CancellationTokenSource();
                using var http = new HttpClient(new ResourceHttp(_ =>
                {
                    if (scenario == "cancel") { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
                    if (scenario == "redirect") { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("http://127.0.0.1/private"); return r; }
                    byte[] payload = scenario switch { "hash" => Enumerable.Repeat((byte)0, data.Length).ToArray(), "extra" => [.. data, 1], "short" => data[..^1], _ => data };
                    var content = new StreamContent(new MemoryStream(payload)); content.Headers.ContentLength = data.Length;
                    return new(HttpStatusCode.OK) { Content = content };
                }));
                var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder); TaskCenterStateContract.DeclareState(builder);
                var store = builder.Build(); var tasks = new TaskCenterService(store);
                var service = new ResourceDownloadService(catalog, new(store), tasks, http);
                bool failed = false;
                try { await service.DownloadAsync(new(ResourceProvider.Modrinth, "Project", "Version", directory), cancel.Token); }
                catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { failed = true; }
                AssertEqual(scenario != "success", failed);
                if (scenario == "success") AssertTrue((await File.ReadAllBytesAsync(Path.Combine(directory, name))).SequenceEqual(data));
                else if (scenario == "overwrite") AssertEqual("keep", await File.ReadAllTextAsync(Path.Combine(directory, name)));
                else AssertFalse(File.Exists(Path.Combine(directory, name)));
                AssertFalse(Directory.EnumerateFiles(directory, ".nexa-resource-*").Any());
                AssertEqual(0, store.Read<TaskCenterSummary>(store.Resolve(TaskCenterStateContract.SummaryKey)).Value!.ActiveCount);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class CatalogStub(Func<ResourceSearchQuery, CancellationToken, Task<ResourceSearchResult>> search) : IResourceCatalogSource
    {
        internal ResourceFile? File { get; init; }
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => search(query, token);
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(new("Project", "Test", "", "", 1, "https://modrinth.com/project/Project"), "",
            [new("Version", "Version", "1", "release", [], [], "", "https://modrinth.com/project/Project/version/Version") { ProjectId = "Project", File = File }]));
    }
}
