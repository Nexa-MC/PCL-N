using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourcePagesRenewCachedMetadataOnceAndRetireLateDetail()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var project = new ResourceProject("Cached", "Cached result", "", "", 1, "https://modrinth.com/project/Cached")
        { Sources = [new(ResourceProvider.Modrinth, "Cached")] };
        var renewedSearch = new TaskCompletionSource<ResourceSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewedDetail = new TaskCompletionSource<ResourceDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ResourceSearchQuery> searches = []; int details = 0; CancellationToken detailToken = default;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, async (query, _) =>
        {
            searches.Add(query);
            if (query.WaitForRefresh) return XsrResult.Success(await renewedSearch.Task);
            return XsrResult.Success(new ResourceSearchResult([project with { Title = query.Text == "new" ? "New query result" : project.Title }], 1, 0)
            { IsStale = query.Text != "new", Notice = query.Text == "new" ? null : "缓存资料" });
        });
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail, async (query, token) =>
        {
            details++;
            if (query.WaitForRefresh) { detailToken = token; return XsrResult.Success(await renewedDetail.Task); }
            return XsrResult.Success(new ResourceDetail(project with { Title = "Cached detail" }, "MIT", []) { IsStale = true, Notice = "缓存资料" });
        });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(page.Page);
        var scene = fixture.Shell.Render(new(1000, 700));
        void Pump(Func<bool> done) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1000, 700)); return done(); }, TimeSpan.FromSeconds(5)));
        Pump(() => scene.Nodes.Any(node => node.Text == "Cached result") && searches.Count == 2);
        AssertTrue(searches[1].WaitForRefresh);
        var input = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        fixture.Shell.Renderer.SetTextInputValue(input, "keep draft");
        for (int i = 0; i < 5; i++) fixture.Shell.Render(new(1000, 700));
        AssertEqual(2, searches.Count);
        renewedSearch.SetResult(new([project with { Title = "Fresh result" }], 1, 0));
        Pump(() => scene.Nodes.Any(node => node.Text == "Fresh result"));
        AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertEqual("keep draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.Cached"));
        Pump(() => scene.Nodes.Any(node => node.Text == "Cached detail") && details == 2);
        for (int i = 0; i < 5; i++) fixture.Shell.Render(new(1000, 700));
        AssertEqual(2, details);
        fixture.Shell.Stage.Navigation.Pop(); fixture.Shell.Render(new(1000, 700));
        AssertTrue(detailToken.IsCancellationRequested);
        fixture.Shell.Renderer.SetTextInputValue(input, "new");
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        Pump(() => scene.Nodes.Any(node => node.Text == "New query result"));
        renewedDetail.SetResult(new(project with { Title = "Late old detail" }, "MIT", []));
        scene = fixture.Shell.Render(new(1000, 700));
        AssertFalse(scene.Nodes.Any(node => node.Text == "Late old detail"));
        AssertEqual(3, searches.Count);
    }
}
