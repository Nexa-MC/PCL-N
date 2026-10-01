using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourcesPageDownloadsByIdentityAndProjectsChineseText()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var feedback = new DesktopFeedbackService();
        var project = new ResourceProject("Project", "Original", "Summary", "Author", 1, "https://modrinth.com/project/Project")
        { ChineseName = "中文名称", Sources = [new(ResourceProvider.Modrinth, "Project"), new(ResourceProvider.CurseForge, "123")] };
        ResourceDetailQuery? detailQuery = null;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([project], 1, 0))));
        queries.Register<ResourceTranslationQuery, ResourceTranslation>(ResourceCatalogContract.Translate, (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceTranslation("中文简介"))));
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail, (query, token) =>
        {
            detailQuery = query;
            return ValueTask.FromResult(XsrResult.Success(new ResourceDetail(project, "MIT", [new ResourceVersion("File", "Version", "1", "正式版", [], [], "", "https://modrinth.com/project/Project")
            { Provider = ResourceProvider.CurseForge, ProjectId = "123", File = new("mod.jar", "https://edge.forgecdn.net/files/mod.jar", 3, null, null) }])));
        });
        ResourceDownloadCommand? downloaded = null;
        var commands = new XsrCommandRouterBuilder();
        commands.Register<ResourceDownloadCommand>(ResourceCatalogContract.Download, (command, token) => { downloaded = command; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => throw new InvalidOperationException("Download must use the command route."));
        page.ConfigureDownloads(commands.Build(new NoopDispatchObserver()), () => Task.FromResult<string?>("chosen-folder"), feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        var scene = fixture.Shell.Render(new(760, 500));
        scene = fixture.Shell.Render(new(760, 500));
        AssertTrue(scene.Nodes.Any(node => node.Text == "中文名称"));
        AssertTrue(scene.Nodes.Any(node => node.Text == "中文简介"));
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.Project"));
        fixture.Shell.Render(new(760, 500)); AssertEqual(2, detailQuery!.Sources.Count);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDownload.File"));
        fixture.Shell.Render(new(760, 500));
        AssertTrue(SpinWait.SpinUntil(() => downloaded is not null, TimeSpan.FromSeconds(3)));
        AssertEqual(ResourceProvider.CurseForge, downloaded!.Provider); AssertEqual("123", downloaded.ProjectId);
        AssertEqual("File", downloaded.VersionId); AssertEqual("chosen-folder", downloaded.DestinationDirectory);
    }

    private static void ResourceIconsArriveWithoutRebuildingSearchOrRows()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var icon = new TaskCompletionSource<ResourceIconResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult(
                [new("WithIcon", "With icon", "", "", 1, "https://modrinth.com/project/WithIcon") { IconUrl = "https://cdn.modrinth.com/data/test/icon.png" }], 1, 0))));
        queries.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon, async (query, token) => XsrResult.Success(await icon.Task));
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        fixture.Shell.Renderer.ReducedMotion = true;
        var scene = fixture.Shell.Render(new(1000, 650));
        var row = FindByKey(fixture.Shell, scene, "ResourceProject.WithIcon").Entity;
        var search = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        var image = FindByKey(fixture.Shell, scene, "ResourceProjectIcon").Entity;
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(image)!.Raster is null);
        var png = Nexa.Core.Media.PngImage.TryCreate(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
        icon.SetResult(new(png));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return fixture.Shell.Tree.GetComponent<XsrUiImage>(image)!.Raster is not null;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual(row, FindByKey(fixture.Shell, scene, "ResourceProject.WithIcon").Entity);
        AssertEqual(search, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
    }
    private static void ResourcePagesReleaseImagesAndRetireCanceledIconGenerations()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var home = fixture.Shell.Stage.Navigation.Current;
        var project = new ResourceProject("OwnedIcon", "Owned icon", "", "", 1, "https://modrinth.com/project/OwnedIcon")
        { IconUrl = "https://cdn.modrinth.com/data/test/icon.png" };
        List<(CancellationToken Token, TaskCompletionSource<ResourceIconResult> Result)> reads = [];
        int searches = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, token) =>
        {
            searches++;
            return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([project], 1, 0)));
        });
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail,
            (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceDetail(project, "MIT", []))));
        queries.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon, async (query, token) =>
        {
            TaskCompletionSource<ResourceIconResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reads.Add((token, result));
            return XsrResult.Success(await result.Task); // Deliberately ignores cancellation to exercise late completion.
        });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        var listIcon = FindByKey(fixture.Shell, scene, "ResourceProjectIcon").Entity;
        var row = FindByKey(fixture.Shell, scene, "ResourceProject.OwnedIcon").Entity;
        var search = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        fixture.Shell.Renderer.SetTextInputValue(search, "kept draft");
        var png = Nexa.Core.Media.PngImage.TryCreate(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="))!;
        AssertEqual(1, reads.Count);
        Complete(0, listIcon);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.OwnedIcon"));
        scene = fixture.Shell.Render(new(1000, 650));
        var detailIcon = FindByKey(fixture.Shell, scene, "ResourceDetailIcon").Entity;
        AssertTrue(Image(listIcon).Raster is null);
        AssertTrue(reads[0].Token.IsCancellationRequested);
        AssertEqual(2, reads.Count);
        Complete(1, detailIcon);
        fixture.Shell.Stage.Navigation.Pop();
        scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(Image(detailIcon).Raster is null);
        AssertEqual(3, reads.Count);
        AssertEqual(row, FindByKey(fixture.Shell, scene, "ResourceProject.OwnedIcon").Entity);
        AssertEqual(search, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertEqual("kept draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(search)!.ReadDraft());
        fixture.Shell.Stage.Navigation.Replace(home);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(reads[2].Token.IsCancellationRequested);
        reads[2].Result.SetResult(new(png));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(Image(listIcon).Raster is null);
        AssertTrue(Image(detailIcon).Raster is null);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(4, reads.Count); // Only the current list is requested; the old detail remains a placeholder.
        AssertEqual(1, searches);
        AssertEqual(row, FindByKey(fixture.Shell, scene, "ResourceProject.OwnedIcon").Entity);
        AssertTrue(Image(listIcon).Raster is null);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        scene = fixture.Shell.Render(new(1000, 650));
        var replacement = FindByKey(fixture.Shell, scene, "ResourceProjectIcon").Entity;
        AssertFalse(fixture.Shell.Tree.IsAlive(listIcon));
        AssertTrue(reads[3].Token.IsCancellationRequested);
        AssertEqual(5, reads.Count); // No dead row or hidden detail is requested during replacement.
        reads[3].Result.SetResult(new(png));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(Image(replacement).Raster is null);
        Complete(4, replacement);
        page.Dispose();
        AssertTrue(Image(replacement).Raster is null);
        AssertTrue(reads[4].Token.IsCancellationRequested);

        XsrUiImage Image(XsrUiEntityId entity) => fixture.Shell.Tree.GetComponent<XsrUiImage>(entity)!;
        void Complete(int index, XsrUiEntityId entity)
        {
            var fresh = Nexa.Core.Media.PngImage.TryCreate(png.Bytes.Span)!;
            reads[index].Result.SetResult(new(fresh));
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                fixture.Shell.Render(new(1000, 650));
                return Image(entity).Raster is not null;
            }, TimeSpan.FromSeconds(5)));
            AssertTrue(ReferenceEquals(fresh, Image(entity).Raster!.Image));
        }
    }

    private static void ResourcesPageUsesServiceQueriesAndPreservesSearch()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var source = new ResourceSource();
        using var runtime = ResourceCatalogRuntimeComposer.Compose(source);
        List<Uri> opened = [];
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, runtime.Queries, fixture.Store, opened.Add);
        var instanceQueries = new XsrQueryRouterBuilder();
        instanceQueries.Register<MinecraftInstallEditQuery, MinecraftInstallEditSnapshot>(MinecraftInstallEditContract.Query,
            (query, token) => ValueTask.FromResult(XsrResult.Success(new MinecraftInstallEditSnapshot(query.RootDirectory, query.InstanceId, "1.21.1", [], "fingerprint"))));
        page.ConfigureInstanceFilter(instanceQueries.Build(new NoopDispatchObserver()), () => new("root", "renamed-instance"));
        fixture.Controller.ResourcesPage = page.Page;
        fixture.Shell.Renderer.ReducedMotion = true;
        Emit(fixture.Intents, "ui.navigation.community");
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(page.Page, fixture.Shell.Stage.Navigation.Current);
        var input = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        var listBounds = FindByKey(fixture.Shell, scene, "ResourceList").Rect;
        var layoutBounds = FindByKey(fixture.Shell, scene, "ResourcesLayout").Rect;
        var footerBounds = FindByKey(fixture.Shell, scene, "ResourcePagination").Rect;
        AssertTrue(listBounds.Y - layoutBounds.Y <= 90);
        AssertTrue(layoutBounds.Y + layoutBounds.Height - footerBounds.Y - footerBounds.Height <= 1);
        fixture.Shell.Renderer.SetTextInputValue(input, "Sodium");
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("Sodium", source.Last!.Text);
        AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertEqual("Sodium", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        var card = FindByKey(fixture.Shell, scene, "ResourceProject.Valid123");
        var body = FindByKey(fixture.Shell, scene, "ResourceProject.Valid123.Body");
        AssertTrue(body.Rect.X > card.Rect.X);
        AssertTrue(body.Rect.X + body.Rect.Width < card.Rect.X + card.Rect.Width);
        AssertTrue(card.Rect.Height <= 84);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceCurrentInstance"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("1.21.1", source.Last!.GameVersion);
        AssertEqual("", source.Last.Loader); // Vanilla must not default to enum value zero (Forge).
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.Valid123"));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(page.DetailPage, fixture.Shell.Stage.Navigation.Current);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDownload.V1"));
        fixture.Shell.Render(new(1000, 650));
        AssertEqual("https://modrinth.com/project/Valid123/version/V1", opened.Single().AbsoluteUri);
        fixture.Shell.Stage.Navigation.Pop();
        scene = fixture.Shell.Render(new(760, 500));
        AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        AssertTrue(FindByKey(fixture.Shell, scene, "ResourceList").Rect.Width > 500);
        var searchBounds = FindByKey(fixture.Shell, scene, "ResourceSearch").Rect;
        AssertTrue(searchBounds.Width >= 100);
    }

    private sealed class ResourceSource : IResourceCatalogSource
    {
        internal ResourceSearchQuery? Last { get; private set; }
        private static ResourceProject Project => new("Valid123", "Sodium", "Renderer", "Author", 100, "https://modrinth.com/project/Valid123");
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
        { Last = query; return Task.FromResult(new ResourceSearchResult([Project], 1, query.Page)); }
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => Task.FromResult(new ResourceDetail(Project, "MIT",
            [new("V1", "One", "1", "正式版", ["1.21.1"], ["fabric"], "2026-01-01", "https://modrinth.com/project/Valid123/version/V1")]));
    }

    private static void ResourcesPageDiscardsSupersededSearchAndRestoresNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var source = new DeferredResourceSource();
        using var runtime = ResourceCatalogRuntimeComposer.Compose(source);
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, runtime.Queries, fixture.Store, _ => { });
        fixture.Controller.ResourcesPage = page.Page;
        fixture.Shell.Renderer.ReducedMotion = true;
        Emit(fixture.Intents, "ui.navigation.community");
        var scene = fixture.Shell.Render(new(1000, 650));
        var first = source.Calls[0];
        var search = FindByKey(fixture.Shell, scene, "ResourceSearch").Entity;
        fixture.Shell.Renderer.SetTextInputValue(search, "new");
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(first.Token.IsCancellationRequested);
        source.Calls[1].Completion.SetResult(new([new("New", "New result", "", "", 1, "https://modrinth.com/project/New")], 1, 0));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return scene.Nodes.Any(node => node.Text == "New result");
        }, TimeSpan.FromSeconds(5)));
        first.Completion.SetResult(new([new("Old", "Old result", "", "", 1, "https://modrinth.com/project/Old")], 1, 0));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(node => node.Text == "Old result"));
        AssertEqual(search, FindByKey(fixture.Shell, scene, "ResourceSearch").Entity);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
        fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(source.Calls[^1].Token.IsCancellationRequested);
    }
    private sealed class DeferredResourceSource : IResourceCatalogSource
    {
        internal List<(TaskCompletionSource<ResourceSearchResult> Completion, CancellationToken Token)> Calls { get; } = [];
        public Task<ResourceSearchResult> SearchAsync(ResourceSearchQuery query, CancellationToken token)
        {
            var completion = new TaskCompletionSource<ResourceSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Add((completion, token)); return completion.Task;
        }
        public Task<ResourceDetail> DetailAsync(ResourceDetailQuery query, CancellationToken token) => throw new InvalidOperationException();
    }
}
