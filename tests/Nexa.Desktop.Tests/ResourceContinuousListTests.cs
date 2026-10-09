using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourceContinuousListAppendsOnceAndRetainsRowsScrollAndDraft()
    {
        using var f = new ContinuousResourceHarness();
        f.Complete(0, ContinuousProjects("First", 20), hasMore: true);
        f.Pump(() => f.Rows.Length > 0);
        var search = FindByKey(f.Fixture.Shell, f.Scene, "ResourceSearch").Entity;
        f.Fixture.Shell.Renderer.SetTextInputValue(search, "unsubmitted draft");
        f.Fixture.Shell.Renderer.SetTextInputValue(FindByKey(f.Fixture.Shell, f.Scene, "ResourceGame").Entity, "unsubmitted game");
        f.Fixture.Shell.Renderer.SetTextInputValue(FindByKey(f.Fixture.Shell, f.Scene, "ResourceLoader").Entity, "unsubmitted loader");
        f.ScrollToBottom();
        f.Pump(() => f.Reads.Count == 2);
        var anchor = f.Row("First19");
        double offset = f.Snapshot.OffsetY;
        double height = f.Snapshot.ContentHeight;
        AssertTrue(offset > 0);
        AssertEqual(1, f.Reads[1].Query.Page);
        AssertEqual(f.Reads[0].Query with { Page = 1 }, f.Reads[1].Query);
        AssertEqual("", f.Reads[1].Query.Text);
        AssertEqual("", f.Reads[1].Query.GameVersion);
        AssertEqual("", f.Reads[1].Query.Loader);
        f.RenderFrames(6);
        AssertEqual(2, f.Reads.Count);
        f.Complete(1, ContinuousProjects("Second", 20), hasMore: false);
        f.Pump(() => f.Snapshot.ContentHeight > height);
        AssertEqual(anchor, f.Row("First19"));
        AssertClose(offset, f.Snapshot.OffsetY);
        AssertEqual(search, FindByKey(f.Fixture.Shell, f.Scene, "ResourceSearch").Entity);
        AssertEqual("unsubmitted draft", f.Fixture.Shell.Tree.GetComponent<XsrUiTextInput>(search)!.ReadDraft());
        AssertFalse(f.NextEnabled);

        f.Click("ResourceDetails.First19");
        f.Pump(() => f.Fixture.Shell.Stage.Navigation.Current == f.Page.DetailPage);
        f.RenderFrames(6);
        AssertEqual(2, f.Reads.Count);
        f.Fixture.Shell.Stage.Navigation.Pop();
        f.RenderFrames(2);
        AssertEqual(anchor, f.Row("First19"));
        AssertClose(offset, f.Snapshot.OffsetY);
        AssertEqual("unsubmitted draft", f.Fixture.Shell.Tree.GetComponent<XsrUiTextInput>(search)!.ReadDraft());
    }

    private static void ResourceContinuousListPreservesRowsAndExplicitlyRetriesTheSamePage()
    {
        using var f = new ContinuousResourceHarness();
        f.Complete(0, ContinuousProjects("Kept", 20), hasMore: true);
        f.Pump(() => f.Rows.Length > 0);
        f.ScrollToBottom();
        f.Pump(() => f.Reads.Count == 2);
        var anchor = f.Row("Kept19");
        double offset = f.Snapshot.OffsetY;
        f.Reads[1].Reply.SetResult(XsrResult.Failure<ResourceSearchResult>(new(XsrErrorKind.Rejected,
            XsrSemanticId.Parse("fixture.resource.offline"), "Provider unavailable")));
        f.Pump(() => f.NextEnabled);
        f.RenderFrames(8);
        AssertEqual(2, f.Reads.Count);
        AssertEqual(anchor, f.Row("Kept19"));
        AssertClose(offset, f.Snapshot.OffsetY);
        f.Click("ResourceNext");
        f.Pump(() => f.Reads.Count == 3);
        AssertEqual(f.Reads[1].Query, f.Reads[2].Query);
        // A provider response for another page cannot move this cursor or replace retained rows.
        f.Reads[2].Reply.SetResult(XsrResult.Success(new ResourceSearchResult(ContinuousProjects("Wrong", 20), 100, 2) { HasMore = true }));
        f.Pump(() => f.NextEnabled);
        AssertFalse(f.Scene.Nodes.Any(node => node.Text?.StartsWith("Wrong", StringComparison.Ordinal) == true));
        AssertEqual(anchor, f.Row("Kept19"));
        f.RenderFrames(6);
        AssertEqual(3, f.Reads.Count);
        f.Click("ResourceNext");
        f.Pump(() => f.Reads.Count == 4);
        AssertEqual(1, f.Reads[3].Query.Page);
        double retainedHeight = f.Snapshot.ContentHeight;
        f.Complete(3, ContinuousProjects("Recovered", 20), hasMore: false);
        f.Pump(() => f.Snapshot.ContentHeight > retainedHeight);
        AssertFalse(f.NextEnabled);
        AssertEqual(anchor, f.Row("Kept19"));
        AssertClose(offset, f.Snapshot.OffsetY);
        f.ScrollToBottom();
        f.Pump(() => f.Scene.Nodes.Any(node => node.Text == "Recovered19"));
        f.RenderFrames(6);
        AssertEqual(4, f.Reads.Count);
    }

    private static void ResourceContinuousListRetiresSearchAndSourceGenerations()
    {
        using var f = new ContinuousResourceHarness();
        f.Complete(0, ContinuousProjects("Old", 20), hasMore: true);
        f.Pump(() => f.Rows.Length > 0);
        f.ScrollToBottom();
        f.Pump(() => f.Reads.Count == 2);
        var search = FindByKey(f.Fixture.Shell, f.Scene, "ResourceSearch").Entity;
        f.Fixture.Shell.Renderer.SetTextInputValue(search, "replacement");
        f.Click("ResourceSearchButton");
        f.Pump(() => f.Reads.Count == 3);
        AssertTrue(f.Reads[1].Token.IsCancellationRequested);
        AssertEqual(0, f.Reads[2].Query.Page);
        AssertEqual("replacement", f.Reads[2].Query.Text);
        AssertClose(0, f.Snapshot.OffsetY);
        f.Complete(1, ContinuousProjects("LateOld", 20), hasMore: false);
        f.RenderFrames(3);
        AssertFalse(f.Scene.Nodes.Any(node => node.Text?.StartsWith("LateOld", StringComparison.Ordinal) == true));
        f.Click("ResourceNetwork.1");
        f.Pump(() => f.Reads.Count == 4);
        AssertTrue(f.Reads[2].Token.IsCancellationRequested);
        AssertEqual(0, f.Reads[3].Query.Page);
        AssertEqual("replacement", f.Reads[3].Query.Text);
        AssertFalse(f.Reads[3].Query.MirrorFirst);
        f.Complete(2, ContinuousProjects("LateMirror", 20), hasMore: false);
        f.Complete(3, ContinuousProjects("Official", 20), hasMore: true);
        f.Pump(() => f.Scene.Nodes.Any(node => node.Text == "Official0"));
        AssertFalse(f.Scene.Nodes.Any(node => node.Text?.StartsWith("LateMirror", StringComparison.Ordinal) == true));
        AssertFalse(f.Scene.Nodes.Any(node => node.Text?.StartsWith("Old", StringComparison.Ordinal) == true));
        AssertTrue(f.NextEnabled);
        f.RenderFrames(6);
        AssertEqual(4, f.Reads.Count);
        f.ScrollToBottom();
        f.Pump(() => f.Reads.Count == 5);
        AssertEqual(1, f.Reads[4].Query.Page);
        AssertTrue(f.Fixture.Foundation.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
        AssertTrue(f.Fixture.Foundation.Commands.Dispatch(set, new SettingsMutation("network.resource-source", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, "mirrors-first"))).Completion.GetAwaiter().GetResult().IsSuccess);
        f.Pump(() => f.Reads.Count == 6);
        AssertTrue(f.Reads[4].Token.IsCancellationRequested);
        AssertEqual(0, f.Reads[5].Query.Page);
        AssertEqual("replacement", f.Reads[5].Query.Text);
        AssertFalse(f.Reads[5].Query.MirrorFirst); // The global override retains the submitted local preference.
        AssertFalse(f.Fixture.Shell.Tree.GetComponent<XsrUiInput>(f.Page.Find("ResourceNetwork.1"))!.Enabled);
        AssertTrue(f.Fixture.Shell.Tree.GetComponent<XsrUiSelection>(f.Page.Find("ResourceNetwork.0"))!.IsSelected);
        f.Complete(4, ContinuousProjects("LatePolicy", 20), hasMore: false);
        f.Complete(5, ContinuousProjects("GlobalMirror", 20), hasMore: false);
        f.Pump(() => f.Scene.Nodes.Any(node => node.Text == "GlobalMirror0"));
        AssertFalse(f.Scene.Nodes.Any(node => node.Text?.StartsWith("LatePolicy", StringComparison.Ordinal) == true));
        AssertFalse(f.NextEnabled);
        f.RenderFrames(6);
        AssertEqual(6, f.Reads.Count);
    }

    private static void ResourceContinuousListKeepsHiddenBatchesAndSourceQualifiedIdentities()
    {
        using var f = new ContinuousResourceHarness();
        var libraries = ContinuousProjects("Library", 20).Select(project => project with { IsLibrary = true }).ToArray();
        f.Complete(0, libraries, hasMore: true);
        f.Pump(() => f.Rows.Length > 0);
        f.Click("ResourceHideLibraries");
        f.Pump(() => f.Reads.Count == 2);
        AssertEqual(1, f.Reads[1].Query.Page);
        var a = ContinuousProject("123", "Unrelated equal title", ResourceProvider.Modrinth);
        var b = ContinuousProject("123", "Unrelated equal title", ResourceProvider.CurseForge);
        var c = ContinuousProject("Different", "Unrelated equal title", ResourceProvider.Modrinth);
        f.Complete(1, [a, a, b, c], hasMore: true);
        f.Pump(() => f.Scene.Nodes.Count(node => node.Text == "Unrelated equal title") == 3);
        AssertEqual(3, f.Rows.Length);
        // A short visible projection may admit one more page automatically.
        if (f.Reads.Count == 2) f.Click("ResourceNext");
        f.Pump(() => f.Reads.Count == 3);
        AssertEqual(2, f.Reads[2].Query.Page);
        // Empty raw provider data terminates even when the provider advertises HasMore.
        f.Complete(2, [], hasMore: true);
        f.Pump(() => !f.NextEnabled);
        f.RenderFrames(6);
        AssertEqual(3, f.Reads.Count);
        AssertEqual(3, f.Rows.Length);
        var sourceCaption = f.Scene.Nodes.Single(node => node.Text?.StartsWith("CurseForge  ·", StringComparison.Ordinal) == true).Entity;
        var sourceRow = sourceCaption;
        while (!f.Rows.Contains(sourceRow)) sourceRow = f.Fixture.Shell.Tree.Parent(sourceRow);
        XsrUiEntityId details = default;
        f.Fixture.Shell.Tree.Walk(sourceRow, entity =>
        {
            if (f.Fixture.Shell.Tree.Name(entity).StartsWith("ResourceDetails.", StringComparison.Ordinal)) details = entity;
            return true;
        });
        AssertTrue(details.IsAssigned);
        Emit(f.Fixture.Intents, "ui.resources.action", details);
        f.Pump(() => f.Details.Count == 1);
        AssertEqual("123", f.Details[0].ProjectId);
        AssertEqual(new ResourceReference(ResourceProvider.CurseForge, "123"), f.Details[0].Sources.Single());
        f.Fixture.Shell.Stage.Navigation.Pop();
        f.RenderFrames(2);
        f.Click("ResourceHideLibraries");
        f.RenderFrames(2);
        AssertTrue(f.Snapshot.ContentHeight > f.Snapshot.ViewportHeight);
        AssertEqual(3, f.Reads.Count);
        AssertFalse(f.NextEnabled);

        using var fallback = new ContinuousResourceHarness();
        fallback.Reads[0].Reply.SetResult(XsrResult.Success(new ResourceSearchResult(ContinuousProjects("TotalA", 20), 41, 0)));
        fallback.Pump(() => fallback.Rows.Length > 0);
        AssertTrue(fallback.NextEnabled);
        fallback.Click("ResourceNext");
        fallback.Pump(() => fallback.Reads.Count == 2);
        double firstHeight = fallback.Snapshot.ContentHeight;
        fallback.Reads[1].Reply.SetResult(XsrResult.Success(new ResourceSearchResult(ContinuousProjects("TotalB", 20), 41, 1)));
        fallback.Pump(() => fallback.Snapshot.ContentHeight > firstHeight);
        AssertTrue(fallback.NextEnabled);
        fallback.Click("ResourceNext");
        fallback.Pump(() => fallback.Reads.Count == 3);
        double secondHeight = fallback.Snapshot.ContentHeight;
        fallback.Reads[2].Reply.SetResult(XsrResult.Success(new ResourceSearchResult([ContinuousProject("TotalLast", "Total last")], 41, 2)));
        fallback.Pump(() => fallback.Snapshot.ContentHeight > secondHeight);
        AssertFalse(fallback.NextEnabled);
        fallback.RenderFrames(6);
        AssertEqual(3, fallback.Reads.Count);
    }

    private static void ResourceContinuousListBoundsRowsProjectsAndViewportMedia()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        List<ResourceSearchQuery> searches = [];
        List<(CancellationToken Token, TaskCompletionSource<ResourceIconResult> Reply)> icons = [];
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, _) =>
        {
            searches.Add(query);
            var projects = ContinuousProjects("Page" + query.Page + ".", 20)
                .Select(project => project with { IconUrl = "https://cdn.modrinth.com/data/" + project.Id + "/icon.png" }).ToArray();
            // Null HasMore uses the page/total contract through more than twenty full pages.
            return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult(projects, 10000, query.Page)));
        });
        queries.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon, async (_, token) =>
        {
            var reply = new TaskCompletionSource<ResourceIconResult>();
            icons.Add((token, reply)); return XsrResult.Success(await reply.Task);
        });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(page.Page);
        XsrUiScene scene = fixture.Shell.Render(new(1100, 700));
        void Pump(Func<bool> done) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1100, 700)); return done(); }, TimeSpan.FromSeconds(5)));
        var list = page.Find("ResourceList");
        int Attached() => fixture.Shell.Tree.Children(list).Count(entity => fixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal));
        Pump(() => Attached() > 0 && icons.Count > 0);
        AssertTrue(Attached() <= 48);
        AssertTrue(icons.Count(read => !read.Token.IsCancellationRequested) <= 48);
        var firstIcon = scene.Nodes.First(node => fixture.Shell.Tree.Name(node.Entity) == "ResourceProjectIcon").Entity;
        var pendingIcon = scene.Nodes.Where(node => fixture.Shell.Tree.Name(node.Entity) == "ResourceProjectIcon").Skip(1).First().Entity;
        var png = Nexa.Core.Media.PngImage.TryCreate(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="))!;
        icons[0].Reply.SetResult(new(png));
        Pump(() => fixture.Shell.Tree.GetComponent<XsrUiImage>(firstIcon)!.Raster is not null);
        for (int index = 1; index < 50; index++)
        {
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiInput>(page.Find("ResourceNext"))!.Enabled);
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceNext"));
            Pump(() => searches.Count == index + 1 && (index == 49 || fixture.Shell.Tree.GetComponent<XsrUiInput>(page.Find("ResourceNext"))!.Enabled));
            AssertTrue(Attached() <= 48);
            AssertTrue(icons.Count(read => !read.Token.IsCancellationRequested) <= 48);
        }
        Pump(() => HasKey(fixture.Shell, scene, "ResourceListBudget"));
        AssertEqual(50, searches.Count);
        AssertEqual(49, searches[^1].Page);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(page.Find("ResourceNext"))!.Enabled);
        AssertTrue(!string.IsNullOrWhiteSpace(FindByKey(fixture.Shell, scene, "ResourceListBudget").Text));
        var viewport = FindByKey(fixture.Shell, scene, "ResourceList").Rect;
        AssertTrue(fixture.Shell.Renderer.PointerScroll(new(viewport.X + 20, viewport.Y + 20), 1000000));
        Pump(() => scene.Nodes.Any(node => node.Text == "Page49.19"));
        AssertTrue(Attached() <= 48);
        AssertTrue(icons.Count(read => !read.Token.IsCancellationRequested) <= 48);
        AssertTrue(icons[0].Token.IsCancellationRequested);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(firstIcon)!.Raster is null);
        AssertTrue(icons[1].Token.IsCancellationRequested);
        icons[1].Reply.SetResult(new(png)); // The retired visible row cannot reacquire a late raster.
        for (int index = 0; index < 4; index++) scene = fixture.Shell.Render(new(1100, 700));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(pendingIcon)!.Raster is null);
        AssertEqual(50, searches.Count);

        // Nonempty duplicate-only batches continue without consuming the distinct-project budget.
        using var duplicateFixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        List<int> duplicatePages = [];
        var duplicateProject = ContinuousProject("Repeated", "Repeated project");
        var duplicateQueries = new XsrQueryRouterBuilder();
        duplicateQueries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, _) =>
        {
            duplicatePages.Add(query.Page);
            return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([duplicateProject], 10000, query.Page) { HasMore = true }));
        });
        using var duplicatePage = new ResourcesPageController(duplicateFixture.Shell, duplicateFixture.Intents,
            duplicateQueries.Build(new NoopDispatchObserver()), duplicateFixture.Store, _ => { });
        duplicateFixture.Shell.Renderer.ReducedMotion = true;
        duplicateFixture.Shell.Stage.Navigation.Replace(duplicatePage.Page);
        var duplicateScene = duplicateFixture.Shell.Render(new(1100, 700));
        var duplicateList = duplicatePage.Find("ResourceList");
        for (int frame = 0; frame < 1500 && duplicatePages.Count < 500; frame++)
        {
            duplicateScene = duplicateFixture.Shell.Render(new(1100, 700));
            AssertTrue(duplicatePages.Count <= 500);
            AssertTrue(duplicateFixture.Shell.Tree.Children(duplicateList)
                .Count(entity => duplicateFixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal)) <= 1);
        }
        AssertEqual(500, duplicatePages.Count);
        AssertTrue(duplicatePages.SequenceEqual(Enumerable.Range(0, 500)));
        for (int frame = 0; frame < 6; frame++) duplicateScene = duplicateFixture.Shell.Render(new(1100, 700));
        AssertEqual(500, duplicatePages.Count);
        AssertFalse(duplicateFixture.Shell.Tree.GetComponent<XsrUiInput>(duplicatePage.Find("ResourceNext"))!.Enabled);
        AssertEqual(1, duplicateScene.Nodes.Count(node => node.Text == "Repeated project"));
        AssertEqual(duplicateFixture.Shell.Renderer.LocalizeText("已达到列表显示上限，请缩小搜索范围。"),
            FindByKey(duplicateFixture.Shell, duplicateScene, "ResourceListBudget").Text);
    }

    private static void ResourceContinuousListRenewsEachRetainedPageWithoutReplacingOthers()
    {
        using var f = new ContinuousResourceHarness();
        f.Complete(0, ContinuousProjects("CachedA", 20), hasMore: true, stale: true);
        f.Pump(() => f.Reads.Any(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        var renewalA = f.Reads.FindIndex(read => read.Query.WaitForRefresh && read.Query.Page == 0);
        var search = FindByKey(f.Fixture.Shell, f.Scene, "ResourceSearch").Entity;
        f.Fixture.Shell.Renderer.SetTextInputValue(search, "unsent");
        f.Click("ResourceNext");
        f.Pump(() => f.Reads.Any(read => !read.Query.WaitForRefresh && read.Query.Page == 1));
        var pageB = f.Reads.FindIndex(read => !read.Query.WaitForRefresh && read.Query.Page == 1);
        AssertEqual("", f.Reads[pageB].Query.Text);
        f.Complete(pageB, ContinuousProjects("CachedB", 20), hasMore: false, stale: true);
        f.Pump(() => f.Reads.Any(read => read.Query.WaitForRefresh && read.Query.Page == 1));
        var renewalB = f.Reads.FindIndex(read => read.Query.WaitForRefresh && read.Query.Page == 1);
        f.ScrollToBottom();
        f.Pump(() => f.Scene.Nodes.Any(node => node.Text == "CachedB19"));
        var anchor = f.Row("CachedB19");
        double offset = f.Snapshot.OffsetY;
        var freshA = ContinuousProjects("CachedA", 20).Select(project => project with { Title = "Fresh " + project.Title }).ToArray();
        f.Complete(renewalA, freshA, hasMore: true);
        f.RenderFrames(3);
        AssertEqual(anchor, f.Row("CachedB19"));
        AssertClose(offset, f.Snapshot.OffsetY);
        f.Reads[renewalB].Reply.SetResult(XsrResult.Failure<ResourceSearchResult>(new(XsrErrorKind.Rejected,
            XsrSemanticId.Parse("fixture.resource.renewal_failed"), "Refresh failed")));
        f.RenderFrames(8);
        AssertEqual(anchor, f.Row("CachedB19"));
        AssertTrue(f.Scene.Nodes.Any(node => node.Text == "CachedB19"));
        AssertEqual(1, f.Reads.Count(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        AssertEqual(1, f.Reads.Count(read => read.Query.WaitForRefresh && read.Query.Page == 1));
        AssertEqual("", f.Reads[renewalA].Query.Text);
        AssertEqual("", f.Reads[renewalB].Query.Text);
        f.ScrollToTop();
        f.Pump(() => f.Scene.Nodes.Any(node => node.Text == "Fresh CachedA0"));
        AssertEqual("unsent", f.Fixture.Shell.Tree.GetComponent<XsrUiTextInput>(search)!.ReadDraft());
        AssertEqual(4, f.Reads.Count);
    }

    private static void ResourceContinuousListRenewsOverlappingPagesWithoutLosingSourceFacts()
    {
        var modrinth = new ResourceReference(ResourceProvider.Modrinth, "A");
        var curseforge = new ResourceReference(ResourceProvider.CurseForge, "123");
        var a = ContinuousProject("A", "Original A");
        var b = ContinuousProject("123", "Retained B", ResourceProvider.CurseForge);
        int RequestPage(ContinuousResourceHarness harness, int page)
        {
            int index = harness.Reads.FindIndex(read => !read.Query.WaitForRefresh && read.Query.Page == page);
            if (index >= 0) return index;
            harness.Click("ResourceNext");
            harness.Pump(() => harness.Reads.Any(read => !read.Query.WaitForRefresh && read.Query.Page == page));
            return harness.Reads.FindIndex(read => !read.Query.WaitForRefresh && read.Query.Page == page);
        }
        ResourceDetailQuery Open(ContinuousResourceHarness harness, string id)
        {
            int before = harness.Details.Count;
            harness.Click("ResourceDetails." + id);
            harness.Pump(() => harness.Details.Count == before + 1);
            return harness.Details[^1];
        }

        using var bridged = new ContinuousResourceHarness();
        bridged.Complete(0, [a], hasMore: true, stale: true);
        bridged.Pump(() => bridged.Reads.Any(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        int renewal = bridged.Reads.FindIndex(read => read.Query.WaitForRefresh && read.Query.Page == 0);
        bridged.Complete(RequestPage(bridged, 1), [a, b], hasMore: true);
        bridged.Pump(() => bridged.Rows.Length == 2);
        var owner = bridged.Row("A");
        var bridge = ContinuousProject("Bridge", "Bridge metadata") with { Sources = [modrinth, curseforge] };
        bridged.Complete(RequestPage(bridged, 2), [bridge], hasMore: false);
        bridged.Pump(() => bridged.Rows.Length == 1);
        AssertEqual(owner, bridged.Rows.Single());
        // Removing the first owner cannot discard either later page's real membership.
        bridged.Complete(renewal, [], hasMore: true);
        bridged.RenderFrames(2);
        AssertEqual(1, bridged.Rows.Length);
        AssertEqual(owner, bridged.Rows.Single());
        var associated = Open(bridged, "A");
        AssertEqual("A", associated.ProjectId);
        AssertEqual(2, associated.Sources.Count);
        AssertTrue(associated.Sources.Contains(modrinth));
        AssertTrue(associated.Sources.Contains(curseforge));
        AssertEqual(1, bridged.Reads.Count(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        AssertEqual(4, bridged.Reads.Count);

        using var restored = new ContinuousResourceHarness();
        restored.Complete(0, [a], hasMore: true, stale: true);
        restored.Pump(() => restored.Reads.Any(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        int removedOwner = restored.Reads.FindIndex(read => read.Query.WaitForRefresh && read.Query.Page == 0);
        restored.Complete(RequestPage(restored, 1), [a with { Title = "Retained A" }, b], hasMore: false);
        restored.Pump(() => restored.Rows.Length == 2);
        var aRow = restored.Row("A");
        var bRow = restored.Row("123");
        restored.Complete(removedOwner, [], hasMore: true);
        restored.Pump(() => restored.Scene.Nodes.Any(node => node.Text == "Retained A")
            && restored.Scene.Nodes.Any(node => node.Text == "Retained B"));
        AssertEqual(2, restored.Rows.Length);
        AssertEqual(aRow, restored.Row("A"));
        AssertEqual(bRow, restored.Row("123"));
        AssertFalse(restored.NextEnabled);
        var restoredA = Open(restored, "A");
        AssertEqual("A", restoredA.ProjectId);
        AssertEqual(modrinth, restoredA.Sources.Single());
        restored.Fixture.Shell.Stage.Navigation.Pop();
        restored.RenderFrames(2);
        var restoredB = Open(restored, "123");
        AssertEqual("123", restoredB.ProjectId);
        AssertEqual(curseforge, restoredB.Sources.Single());
        AssertEqual(1, restored.Reads.Count(read => read.Query.WaitForRefresh && read.Query.Page == 0));
        AssertEqual(3, restored.Reads.Count);

        using var rawFixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        List<int> rawPages = [];
        var repeated = Enumerable.Repeat(ContinuousProject("R", "Repeated raw fact"), 1000).ToArray();
        var rawQueries = new XsrQueryRouterBuilder();
        rawQueries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, _) =>
        {
            rawPages.Add(query.Page);
            return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult(repeated, 10000, query.Page) { HasMore = true }));
        });
        using var rawPage = new ResourcesPageController(rawFixture.Shell, rawFixture.Intents,
            rawQueries.Build(new NoopDispatchObserver()), rawFixture.Store, _ => { });
        rawFixture.Shell.Renderer.ReducedMotion = true; rawFixture.Shell.Stage.Navigation.Replace(rawPage.Page);
        var rawScene = rawFixture.Shell.Render(new(1100, 700));
        for (int frame = 0; frame < 100 && !HasKey(rawFixture.Shell, rawScene, "ResourceListBudget"); frame++)
        {
            rawScene = rawFixture.Shell.Render(new(1100, 700));
            AssertTrue(rawPages.Count <= 16);
            AssertTrue(rawFixture.Shell.Tree.Children(rawPage.Find("ResourceList"))
                .Count(entity => rawFixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal)) <= 1);
        }
        AssertTrue(rawPages.SequenceEqual(Enumerable.Range(0, 16)));
        AssertEqual(1, rawScene.Nodes.Count(node => node.Text == "Repeated raw fact"));
        AssertEqual(rawFixture.Shell.Renderer.LocalizeText("已达到列表显示上限，请缩小搜索范围。"),
            FindByKey(rawFixture.Shell, rawScene, "ResourceListBudget").Text);
        AssertFalse(rawFixture.Shell.Tree.GetComponent<XsrUiInput>(rawPage.Find("ResourceNext"))!.Enabled);
        for (int frame = 0; frame < 6; frame++) rawScene = rawFixture.Shell.Render(new(1100, 700));
        AssertEqual(16, rawPages.Count);

        using var textFixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        int textReads = 0;
        var oversized = ContinuousProject("Oversized", "Oversized retained text")
            with
        { Description = new string('x', 4 * 1024 * 1024 + 1) };
        var textQueries = new XsrQueryRouterBuilder();
        textQueries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, _) =>
        {
            textReads++;
            return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([oversized], 10000, query.Page) { HasMore = true }));
        });
        using var textPage = new ResourcesPageController(textFixture.Shell, textFixture.Intents,
            textQueries.Build(new NoopDispatchObserver()), textFixture.Store, _ => { });
        textFixture.Shell.Renderer.ReducedMotion = true; textFixture.Shell.Stage.Navigation.Replace(textPage.Page);
        var textScene = textFixture.Shell.Render(new(1100, 700));
        for (int frame = 0; frame < 8; frame++) textScene = textFixture.Shell.Render(new(1100, 700));
        AssertEqual(1, textReads);
        AssertEqual(0, textFixture.Shell.Tree.Children(textPage.Find("ResourceList"))
            .Count(entity => textFixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal)));
        AssertFalse(textScene.Nodes.Any(node => node.Text == "Oversized retained text"));
        AssertFalse(textFixture.Shell.Tree.GetComponent<XsrUiInput>(textPage.Find("ResourceNext"))!.Enabled);
        AssertEqual(textFixture.Shell.Renderer.LocalizeText("已达到列表显示上限，请缩小搜索范围。"),
            FindByKey(textFixture.Shell, textScene, "ResourceListBudget").Text);
    }

    private static ResourceProject ContinuousProject(string id, string title, ResourceProvider provider = ResourceProvider.Modrinth) =>
        new(id, title, "Description " + id, "Author", 1, (provider == ResourceProvider.CurseForge
            ? "https://www.curseforge.com/minecraft/mc-mods/" : "https://modrinth.com/project/") + id)
        { Sources = [new(provider, id)] };

    private static ResourceProject[] ContinuousProjects(string prefix, int count) =>
        Enumerable.Range(0, count).Select(index => ContinuousProject(prefix + index, prefix + index)).ToArray();

    private sealed class ContinuousResourceRead(ResourceSearchQuery query, CancellationToken token)
    {
        internal ResourceSearchQuery Query { get; } = query;
        internal CancellationToken Token { get; } = token;
        // Deterministic owner-thread completion; the controller still publishes only in a render frame.
        internal TaskCompletionSource<XsrResult<ResourceSearchResult>> Reply { get; } = new();
    }

    private sealed class ContinuousResourceHarness : IDisposable
    {
        internal LaunchPageFixture Fixture { get; } = new(new ImmediateInstanceSource([]));
        internal List<ContinuousResourceRead> Reads { get; } = [];
        internal List<ResourceDetailQuery> Details { get; } = [];
        internal ResourcesPageController Page { get; }
        internal XsrUiScene Scene { get; private set; }
        internal XsrUiEntityId[] Rows => Fixture.Shell.Tree.Children(Page.Find("ResourceList"))
            .Where(entity => Fixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal)).ToArray();
        internal bool NextEnabled => Fixture.Shell.Tree.GetComponent<XsrUiInput>(Page.Find("ResourceNext"))!.Enabled;
        internal XsrUiScrollSnapshot Snapshot
        {
            get
            {
                AssertTrue(Fixture.Shell.Renderer.TryGetScrollSnapshot(Page.Find("ResourceList"), out var snapshot));
                return snapshot;
            }
        }
        internal ContinuousResourceHarness()
        {
            var queries = new XsrQueryRouterBuilder();
            queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, async (query, token) =>
            {
                var read = new ContinuousResourceRead(query, token); Reads.Add(read);
                return await read.Reply.Task; // Late replies deliberately do not honor cancellation.
            });
            queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail, (query, _) =>
            {
                Details.Add(query);
                return ValueTask.FromResult(XsrResult.Success(new ResourceDetail(ContinuousProject(query.ProjectId, "Detail " + query.ProjectId), "MIT", [])));
            });
            queries.Register<ResourceNetworkPolicyQuery, ResourceNetworkPolicySnapshot>(ResourceCatalogContract.NetworkPolicy, (_, _) =>
            {
                var values = Fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values;
                string priority = values.Single(value => value.Key == "network.resource-source").Value.Value!;
                return ValueTask.FromResult(XsrResult.Success(new ResourceNetworkPolicySnapshot(priority, true, null)));
            });
            Page = new(Fixture.Shell, Fixture.Intents, queries.Build(new NoopDispatchObserver()), Fixture.Store, _ => { });
            Fixture.Shell.Renderer.ReducedMotion = true; Fixture.Shell.Stage.Navigation.Replace(Page.Page);
            Scene = Fixture.Shell.Render(new(1100, 700));
            Pump(() => Reads.Count == 1);
        }
        internal void Complete(int index, IReadOnlyList<ResourceProject> projects, bool hasMore, bool stale = false) =>
            Reads[index].Reply.SetResult(XsrResult.Success(new ResourceSearchResult(projects, 10000, Reads[index].Query.Page)
            { HasMore = hasMore, IsStale = stale, Notice = stale ? "缓存资料" : null }));
        internal void Pump(Func<bool> done) => AssertTrue(SpinWait.SpinUntil(() =>
        { Scene = Fixture.Shell.Render(new(1100, 700)); return done(); }, TimeSpan.FromSeconds(5)));
        internal void RenderFrames(int count)
        { for (int index = 0; index < count; index++) Scene = Fixture.Shell.Render(new(1100, 700)); }
        internal void Click(string key) => Emit(Fixture.Intents, "ui.resources.action", Page.Find(key));
        internal XsrUiEntityId Row(string id) => Rows.Single(entity => Fixture.Shell.Tree.Name(entity) == "ResourceProject." + id);
        internal void ScrollToBottom() => Scroll(1000000);
        internal void ScrollToTop() => Scroll(-1000000);
        private void Scroll(double amount)
        {
            var viewport = FindByKey(Fixture.Shell, Scene, "ResourceList").Rect;
            AssertTrue(Fixture.Shell.Renderer.PointerScroll(new(viewport.X + 20, viewport.Y + 20), amount));
            RenderFrames(2);
        }
        public void Dispose() { Page.Dispose(); Fixture.Dispose(); }
    }
}
