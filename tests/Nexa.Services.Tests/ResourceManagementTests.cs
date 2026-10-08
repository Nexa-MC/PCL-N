using Nexa.Services.Resources;
namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ResourceFavoritesPersistByProviderIdentity()
    {
        string directory = CreateTempDirectory();
        try
        {
            string file = Path.Combine(directory, "favorites.json");
            var service = new ResourceFavoritesService(file);
            var project = new ResourceProject("A", "Title", "Summary", "Author", 1000, "https://modrinth.com/project/A")
            { Kind = ResourceKind.Shader, ChineseName = "中文", Sources = [new(ResourceProvider.Modrinth, "A"), new(ResourceProvider.CurseForge, "123")] };
            await service.SetAsync(new(project, true), default);
            await service.SetAsync(new(project with { Title = "Changed" }, true), default);
            var loaded = await new ResourceFavoritesService(file).ReadAsync(default);
            AssertEqual(1, loaded.Projects.Count); AssertEqual("Changed", loaded.Projects[0].Title);
            AssertEqual("中文", loaded.Projects[0].DisplayName); AssertEqual(ResourceKind.Shader, loaded.Projects[0].Kind);
            AssertEqual(2, loaded.Projects[0].Sources.Count);
            await service.SetAsync(new(project with { Sources = [new(ResourceProvider.CurseForge, "123")] }, false), default);
            AssertEqual(0, (await new ResourceFavoritesService(file).ReadAsync(default)).Projects.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async ValueTask ResourceDependenciesAreCompatibleOrderedAndBounded()
    {
        static ResourceVersion Version(string project, string version, params ResourceDependency[] deps) => new(version, project, "1", "正式版", ["1.21.1"], ["fabric"], "2026-01-01", "https://modrinth.com/project/" + project)
        { ProjectId = project, File = new(project + ".jar", "https://cdn.modrinth.com/data/file.jar", 3, null, new string('a', 128)), Dependencies = deps };
        var a = Version("A", "A1", new("B", "B1", "required"), new("D", null, "optional"), new("E", null, "embedded"));
        var b = Version("B", "B1", new ResourceDependency("C", null, "required")); var c = Version("C", "C1");
        var catalog = new DependencyCatalog([a, b, c]);
        var command = new ResourceModInstallCommand(new(ResourceProvider.Modrinth, "A"), "A1", new("root", "instance"));
        var context = new ResourceInstanceContext("1.21.1", "fabric", [], null);
        var plan = await new ResourceDependencyPlanner(catalog).PlanAsync(command, context, default);
        AssertEqual("C,B,A", string.Join(',', plan.Select(v => v.ProjectId)));
        plan = await new ResourceDependencyPlanner(catalog).PlanAsync(command, context with { Installed = [new(new(ResourceProvider.Modrinth, "B"), "B1", "b.jar", "", true)] }, default);
        AssertEqual("C,A", string.Join(',', plan.Select(v => v.ProjectId))); // Existing B still needs its missing C dependency.
        async Task Reject(DependencyCatalog source, ResourceInstanceContext current)
        {
            bool rejected = false;
            try { await new ResourceDependencyPlanner(source).PlanAsync(command, current, default); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
        var cyclic = await new ResourceDependencyPlanner(new DependencyCatalog([a, b with { Dependencies = [new("A", "A1", "required")] }])).PreviewAsync(command, context, default);
        AssertTrue(cyclic.HasCycles); AssertEqual("B,A", string.Join(',', cyclic.Versions.Select(v => v.ProjectId)));
        await Reject(new([a, b with { Games = ["1.20.1"] }]), context);
        await Reject(catalog, context with { Installed = [new(new(ResourceProvider.Modrinth, "B"), "B1", "b.jar.disabled", "", false)] });
        await Reject(new([a with { Dependencies = [new("B", "B1", "required"), new("B", "B2", "required")] }, b, Version("B", "B2")]), context);
        await Reject(new([a with { Dependencies = [new("B", "B1", "required"), new("B", null, "incompatible")] }, b, c]), context);
        await Reject(new([a, b with { File = null }]), context);
        var optionalCatalog = new DependencyCatalog([a, b, c, Version("D", "D1", new("F", null, "optional"), new("G", null, "required")), Version("F", "F1"), Version("G", "G1")]);
        var preview = await new ResourceDependencyPlanner(optionalCatalog).PreviewAsync(command, context, default);
        AssertEqual("D", preview.Optional.Single().Source.ProjectId); AssertFalse(preview.Versions.Any(v => v.ProjectId == "D"));
        preview = await new ResourceDependencyPlanner(optionalCatalog).PreviewAsync(command with { OptionalDependencies = [new(ResourceProvider.Modrinth, "D")] }, context, default);
        AssertEqual("C,B,G,D,A", string.Join(',', preview.Versions.Select(v => v.ProjectId))); AssertEqual("D,F", string.Join(',', preview.Optional.Select(v => v.Source.ProjectId)));
    }
    private static async ValueTask ResourceSearchCacheAvoidsDuplicateProviderWork()
    {
        int calls = 0;
        var provider = new CatalogStub((query, _) => { calls++; return Task.FromResult(new ResourceSearchResult([new("Project", "Project", "", "", 0, "https://modrinth.com/project/Project") { Sources = [new(ResourceProvider.Modrinth, "Project")] }], 1, query.Page)); });
        var catalog = new MergedResourceCatalog(provider, provider);
        await catalog.SearchAsync(new(Text: "钠"), default);
        int first = calls;
        await catalog.SearchAsync(new(Text: "钠"), default);
        AssertEqual(first, calls);
        await catalog.SearchAsync(new(Text: "钠") { MirrorFirst = false }, default);
        AssertTrue(calls > first);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        bool canceled = false;
        try { await catalog.SearchAsync(new(Text: "钠"), stop.Token); } catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled);
    }
    private sealed class DependencyCatalog(ResourceVersion[] versions) : IResourceCatalogSource, IResourceFileSource
    {
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(new(query.ProjectId, query.ProjectId, "", "", 0, "https://modrinth.com/project/" + query.ProjectId), "", versions.Where(v => v.ProjectId == query.ProjectId).ToArray()));
        public Task<ResourceVersion?> ReadVersionAsync(ResourceDownloadCommand command, CancellationToken token) => Task.FromResult(versions.FirstOrDefault(v => v.Id == command.VersionId && (command.ProjectId.Length == 0 || v.ProjectId == command.ProjectId)));
    }
}
