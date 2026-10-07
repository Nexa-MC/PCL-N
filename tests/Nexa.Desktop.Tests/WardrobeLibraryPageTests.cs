using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WardrobeLibraryMatchesDevFiltersPaginationAndLinks()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
        List<WardrobeCatalogQuery> requests = [];
        List<Uri> opened = [];
        int accountReads = 0, back = 0;
        var queries = LibraryQueries(fixture, () => accountReads++);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
        {
            requests.Add(query);
            return ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(101, query.Kind)])));
        });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureOpenUrl(opened.Add); page.ConfigureBack(() => back++);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual("LittleSkin", LibraryText(fixture, "WardrobeLibrarySiteName"));
        AssertTrue(LibraryText(fixture, "WardrobeLibraryCard.101.Uploader").Contains("皮肤", StringComparison.Ordinal));
        AssertTrue(LibraryText(fixture, "WardrobeLibraryCard.101.Metadata").Contains("Slim · HD", StringComparison.Ordinal));
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, "WardrobeLibraryCard.101.Name"))!.Localize);
        var stablePreview = FindEntity(fixture.Shell, "WardrobeLibraryCard.101.Preview");
        int initialRequests = requests.Count;
        fixture.Shell.Render(new(810, 1200)); fixture.Shell.Render(new(810, 1200));
        AssertEqual(initialRequests, requests.Count); AssertEqual(1, accountReads);
        AssertEqual(stablePreview, FindEntity(fixture.Shell, "WardrobeLibraryCard.101.Preview"));
        var narrow = fixture.Shell.Render(new(810, 1200));
        AssertEqual(238d, narrow.Nodes.Single(node => fixture.Shell.Tree.Name(node.Entity) == "WardrobeLibraryRail").Rect.Width + 24);
        var wide = fixture.Shell.Render(new(1600, 1200));
        AssertEqual(294d, wide.Nodes.Single(node => fixture.Shell.Tree.Name(node.Entity) == "WardrobeLibraryRail").Rect.Width + 24);
        var normal = fixture.Shell.Render(new(1200, 1200));
        AssertEqual(268d, normal.Nodes.Single(node => fixture.Shell.Tree.Name(node.Entity) == "WardrobeLibraryRail").Rect.Width + 24);
        LibraryEmit(fixture, "ui.wardrobe.library.next", "WardrobeLibraryNext");
        LibraryPump(fixture, () => requests.Count >= 2 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual(2, requests[^1].Page);
        fixture.Shell.Renderer.SetTextInputValue(FindEntity(fixture.Shell, "WardrobeLibrarySearch"), "  Dragon  ");
        LibraryEmit(fixture, "ui.wardrobe.library.search", "WardrobeLibrarySearchButton");
        LibraryPump(fixture, () => requests.Count >= 3 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual("Dragon", requests[^1].Keyword); AssertEqual(1, requests[^1].Page);
        LibraryEmit(fixture, "ui.wardrobe.library.cape", "WardrobeLibraryCape");
        LibraryPump(fixture, () => requests.Count >= 4 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual(WardrobeCatalogKind.Cape, requests[^1].Kind);
        LibraryEmit(fixture, "ui.wardrobe.library.likes", "WardrobeLibraryLikes");
        LibraryPump(fixture, () => requests.Count >= 5 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual(WardrobeCatalogOrder.Likes, requests[^1].Order);
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => requests.Count >= 6 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertTrue(requests[^1].ForceRefresh);
        fixture.Shell.Renderer.SetTextInputValue(FindEntity(fixture.Shell, "WardrobeLibrarySearch"), "");
        LibraryPump(fixture, () => requests.Count >= 7 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertEqual("", requests[^1].Keyword); AssertFalse(requests[^1].ForceRefresh);
        LibraryEmit(fixture, "ui.wardrobe.library.docs", "WardrobeLibraryDocs");
        LibraryEmit(fixture, "ui.wardrobe.library.site", "WardrobeLibraryOpenSite");
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.101.Details");
        LibraryPump(fixture, () => opened.Count == 3);
        AssertEqual("https://docs.littleskin.cn/", opened[0].AbsoluteUri);
        AssertEqual("https://littleskin.cn/", opened[1].AbsoluteUri);
        AssertEqual("https://littleskin.cn/skinlib/show/101", opened[2].AbsoluteUri);
        LibraryEmit(fixture, "ui.wardrobe.library.back", "WardrobeLibraryBack");
        LibraryPump(fixture, () => back == 1);
    }

    private static void WardrobeLibraryAdmitsApplyAndRespectsProviderRestrictions()
    {
        foreach (var kind in new[] { LaunchProfileKind.Microsoft, LaunchProfileKind.NCloud, LaunchProfileKind.Offline, LaunchProfileKind.ThirdParty, LaunchProfileKind.LittleSkin })
        {
            using LaunchPageFixture fixture = LibraryFixture(kind);
            UiLocalizationCatalog localization = new();
            localization.SetLanguage("en");
            fixture.Shell.Renderer.TextLocalizer = localization.Translate;
            AccountWardrobeApplyPublicCommand? applied = null;
            int reads = 0;
            List<Uri> opened = [];
            var queries = LibraryQueries(fixture, () => reads++);
            queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
                ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(201, query.Kind)]))));
            var commands = new XsrCommandRouterBuilder();
            commands.Register<AccountWardrobeApplyPublicCommand>(AccountWardrobeContract.ApplyPublic, (command, _) =>
            { applied = command; return ValueTask.FromResult(XsrResult.Success()); });
            using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
            page.ConfigureOpenUrl(opened.Add);
            fixture.Shell.Stage.Navigation.Replace(page.Page);
            LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems"));
            var skinApply = FindEntity(fixture.Shell, "WardrobeLibraryCard.201.Apply");
            // A valid command still requires its live, enabled, owning button.
            Emit(fixture.Intents, "ui.wardrobe.library.action");
            LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibrarySearchButton");
            fixture.Shell.Tree.GetComponent<XsrUiInput>(skinApply)!.Enabled = false;
            Emit(fixture.Intents, "ui.wardrobe.library.action", skinApply); fixture.Shell.Render(new(810, 1200));
            AssertTrue(applied is null);
            if (kind is LaunchProfileKind.Microsoft or LaunchProfileKind.NCloud or LaunchProfileKind.LittleSkin)
            {
                fixture.Shell.Tree.GetComponent<XsrUiInput>(skinApply)!.Enabled = true;
                Emit(fixture.Intents, "ui.wardrobe.library.action", skinApply);
                LibraryPump(fixture, () => applied is not null);
                AssertEqual("littleskin", applied!.SiteId); AssertEqual(201L, applied.TextureId);
                AssertEqual(kind, applied.Identity.Kind); AssertEqual(AccountWardrobeTextureKind.Skin, applied.Kind);
                LibraryPump(fixture, () => reads >= 2 && LibraryEnabled(fixture, "WardrobeLibraryCard.201.Apply"));
            }
            else
            {
                AssertFalse(LibraryEnabled(fixture, "WardrobeLibraryCard.201.Apply"));
                AssertTrue(LibraryText(fixture, "WardrobeLibraryAccountNotice").Contains(kind == LaunchProfileKind.Offline ? "仅供预览" : "独立授权", StringComparison.Ordinal));
            }
            if (kind == LaunchProfileKind.ThirdParty)
            {
                AssertTrue(LibraryVisible(fixture, "WardrobeLibraryManage"));
                LibraryEmit(fixture, "ui.wardrobe.library.manage", "WardrobeLibraryManage");
                LibraryPump(fixture, () => opened.Count == 1); AssertEqual("https://account.example/manage", opened[0].AbsoluteUri);
            }
            applied = null;
            LibraryEmit(fixture, "ui.wardrobe.library.cape", "WardrobeLibraryCape");
            LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems")
                && fixture.Shell.Tree.IsAlive(FindEntity(fixture.Shell, "WardrobeLibraryCard.201.Metadata")));
            AssertEqual("♥ 42 · Cape · HD", LibraryText(fixture, "WardrobeLibraryCard.201.Metadata"));
            AssertEqual("使用披风", LibraryText(fixture, "WardrobeLibraryCard.201.Apply"));
            bool allowedCape = kind == LaunchProfileKind.LittleSkin;
            bool showCape = kind is not (LaunchProfileKind.Microsoft or LaunchProfileKind.NCloud);
            AssertEqual(showCape, LibraryVisible(fixture, "WardrobeLibraryCard.201.Apply"));
            AssertEqual(allowedCape, LibraryEnabled(fixture, "WardrobeLibraryCard.201.Apply"));
            if (showCape)
            {
                var scene = fixture.Shell.Render(new(810, 1200));
                var applyButton = FindByKey(fixture.Shell, scene, "WardrobeLibraryCard.201.Apply");
                AssertEqual("Use cape", applyButton.Text); AssertEqual(allowedCape, applyButton.IsEnabled);
            }
            // A retired skin-card source cannot apply a cape from a replacement page.
            Emit(fixture.Intents, "ui.wardrobe.library.action", skinApply); fixture.Shell.Render(new(810, 1200));
            AssertTrue(applied is null);
            LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.201.Apply");
            fixture.Shell.Render(new(810, 1200));
            if (allowedCape) { LibraryPump(fixture, () => applied is not null); AssertEqual(AccountWardrobeTextureKind.Cape, applied!.Kind); }
            else AssertTrue(applied is null);
        }
    }

    private static void WardrobeLibraryRetiresLateCataloguesImagesAndAccountReplies()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
        var launchPage = fixture.Shell.Stage.Navigation.Current;
        fixture.Service.AddProfile(new LaunchProfile { Username = "Bob", Kind = LaunchProfileKind.LittleSkin, Uuid = "bob" });
        var firstCatalog = new TaskCompletionSource<XsrResult<WardrobeCatalogPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldImage = new TaskCompletionSource<XsrResult<AccountWardrobeResolvedTextures>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default, oldImageToken = default;
        int catalogReads = 0, imageReads = 0;
        var queries = LibraryQueries(fixture);
        WardrobeCatalogQuery firstRequest = new();
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, token) =>
        {
            if (++catalogReads == 1) { firstRequest = query; firstToken = token; return new(firstCatalog.Task); }
            return ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(302, query.Kind)])));
        });
        queries.Register<WardrobeCatalogPreviewQuery, AccountWardrobeResolvedTextures>(WardrobeCatalogContract.Preview, (_, token) =>
        {
            if (++imageReads == 1) { oldImageToken = token; return new(oldImage.Task); }
            return ValueTask.FromResult(XsrResult.Success(new AccountWardrobeResolvedTextures(null, null, false, null, null)));
        });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => catalogReads == 1);
        LibraryEmit(fixture, "ui.wardrobe.library.cape", "WardrobeLibraryCape");
        LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems") && imageReads == 1);
        AssertTrue(firstToken.IsCancellationRequested);
        firstCatalog.SetResult(XsrResult.Success(LibraryPage(firstRequest, [LibraryItem(301, WardrobeCatalogKind.Skin)])));
        fixture.Shell.Render(new(810, 1200));
        AssertFalse(fixture.Shell.Render(new(810, 1200)).Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "WardrobeLibraryCard.301.Name"));
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        LibraryPump(fixture, () => catalogReads >= 3 && imageReads >= 2 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertTrue(oldImageToken.IsCancellationRequested);
        var currentPreview = FindEntity(fixture.Shell, "WardrobeLibraryCard.302.Preview");
        string currentKey = fixture.Shell.Tree.GetComponent<XsrUiImage>(currentPreview)!.Raster!.Image.Key;
        oldImage.SetResult(XsrResult.Success(new AccountWardrobeResolvedTextures("old", null, true, WardrobeDesktopSkin(), null)));
        fixture.Shell.Render(new(810, 1200));
        AssertEqual(currentKey, fixture.Shell.Tree.GetComponent<XsrUiImage>(currentPreview)!.Raster!.Image.Key);
        var retiredApply = FindEntity(fixture.Shell, "WardrobeLibraryCard.302.Apply");
        fixture.Shell.Stage.Navigation.Replace(launchPage);
        fixture.Shell.Render(new(810, 1200));
        AssertFalse(fixture.Shell.Tree.IsAlive(currentPreview));
        Emit(fixture.Intents, "ui.wardrobe.library.action", retiredApply); fixture.Shell.Render(new(810, 1200));
        AssertEqual(3, catalogReads);
        page.Dispose();

        var delayedAccount = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken accountToken = default;
        AccountWardrobeSnapshot oldAccount = LibrarySnapshot(fixture);
        int accounts = 0;
        var lateQueries = new XsrQueryRouterBuilder();
        lateQueries.Register<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(WardrobeCatalogContract.Sites, (_, _) => ValueTask.FromResult(XsrResult.Success(LibrarySites())));
        lateQueries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) => ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(303, query.Kind)]))));
        lateQueries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, token) =>
        {
            if (++accounts == 1) { accountToken = token; return new(delayedAccount.Task); }
            return ValueTask.FromResult(XsrResult.Success(LibrarySnapshot(fixture)));
        });
        using var latePage = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, lateQueries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(latePage.Page);
        LibraryPump(fixture, () => accounts == 1);
        AssertTrue(fixture.Service.SelectProfile(0) is null);
        LibraryPump(fixture, () => accounts >= 2 && LibraryEnabled(fixture, "WardrobeLibraryCard.303.Apply"));
        AssertTrue(accountToken.IsCancellationRequested);
        delayedAccount.SetResult(XsrResult.Success(oldAccount)); fixture.Shell.Render(new(810, 1200));
        AssertTrue(LibraryEnabled(fixture, "WardrobeLibraryCard.303.Apply"));
    }

    private static void WardrobeLibrarySeparatesLoadingEmptyFailureAndBoundsCards()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Offline);
        var loading = new TaskCompletionSource<XsrResult<WardrobeCatalogPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        WardrobeCatalogQuery initial = new();
        var queries = LibraryQueries(fixture);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
        {
            if (++reads == 1) { initial = query; return new(loading.Task); }
            if (reads == 2) return ValueTask.FromResult(XsrResult.Failure<WardrobeCatalogPage>(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("fixture.library.failure"), "SECRET remote body")));
            int count = reads == 3 ? 64 : 65;
            return ValueTask.FromResult(XsrResult.Success(LibraryPage(query, Enumerable.Range(1, count).Select(i => LibraryItem(i, query.Kind)).ToArray())));
        });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => reads == 1);
        AssertTrue(LibraryVisible(fixture, "WardrobeLibraryLoading"));
        AssertFalse(LibraryVisible(fixture, "WardrobeLibraryEmpty")); AssertFalse(LibraryVisible(fixture, "WardrobeLibraryItems"));
        loading.SetResult(XsrResult.Success(LibraryPage(initial, [])));
        LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryEmpty"));
        AssertEqual("没有找到皮肤", LibraryText(fixture, "WardrobeLibraryEmptyTitle"));
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 2 && LibraryText(fixture, "WardrobeLibraryEmptyTitle") == "皮肤站暂时不可用");
        AssertEqual("API 请求失败", LibraryText(fixture, "WardrobeLibrarySiteStatus"));
        AssertFalse(fixture.Shell.Render(new(810, 1200)).Nodes.Any(n => n.Text?.Contains("SECRET", StringComparison.Ordinal) == true));
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 3 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        int cards = 0;
        fixture.Shell.Tree.Walk(FindEntity(fixture.Shell, "WardrobeLibraryItems"), entity =>
        { if (fixture.Shell.Tree.Name(entity).EndsWith(".Preview", StringComparison.Ordinal)) cards++; return true; });
        AssertEqual(WardrobeLibraryPageController.MaximumVisibleCards, cards);
        AssertTrue(fixture.Shell.Tree.IsAlive(FindEntity(fixture.Shell, "WardrobeLibraryCard.64.Preview")));
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 4 && LibraryVisible(fixture, "WardrobeLibraryEmpty"));
        AssertEqual("皮肤站暂时不可用", LibraryText(fixture, "WardrobeLibraryEmptyTitle"));
    }

    private static void WardrobeLibraryLoadsOnlyVisiblePublicPreviewsAndPreservesViews()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("CN"));
        var imageReads = new List<(long Id, CancellationToken Stop, TaskCompletionSource<XsrResult<AccountWardrobeResolvedTextures>> Reply)>();
        var queries = LibraryQueries(fixture);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, Enumerable.Range(1, 64).Select(i => LibraryItem(i, query.Kind)).ToArray()))));
        queries.Register<WardrobeCatalogPreviewQuery, AccountWardrobeResolvedTextures>(WardrobeCatalogContract.Preview, (query, token) =>
        {
            var reply = new TaskCompletionSource<XsrResult<AccountWardrobeResolvedTextures>>(TaskCreationOptions.RunContinuationsAsynchronously);
            imageReads.Add((query.TextureId, token, reply));
            return new(reply.Task);
        });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(810, 600)); return imageReads.Count > 0; }, TimeSpan.FromSeconds(5)));
        AssertTrue(imageReads.Count <= 4); AssertFalse(imageReads.Any(read => read.Id == 64));
        AssertEqual("请先添加并选择一个账户。", LibraryText(fixture, "WardrobeLibraryAccountNotice"));
        var firstPreview = FindEntity(fixture.Shell, "WardrobeLibraryCard.1.Preview");
        var firstImage = fixture.Shell.Tree.GetComponent<XsrUiImage>(firstPreview)!.Raster;
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.1.View");
        fixture.Shell.Render(new(810, 600));
        AssertEqual("正面 · 切换视角", LibraryText(fixture, "WardrobeLibraryCard.1.View"));
        AssertFalse(ReferenceEquals(firstImage, fixture.Shell.Tree.GetComponent<XsrUiImage>(firstPreview)!.Raster));
        var scene = fixture.Shell.Render(new(810, 600));
        var body = scene.Nodes.Single(node => fixture.Shell.Tree.Name(node.Entity) == "WardrobeLibraryBody");
        AssertTrue(body.Rect.Width > 208 && body.Rect.Height >= 100);
        var preview = scene.Nodes.Single(node => node.Entity == firstPreview);
        AssertTrue(preview.Rect.X >= body.Rect.X && preview.Rect.X + preview.Rect.Width <= body.Rect.X + body.Rect.Width);
        AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "WardrobeLibraryNext" && node.ClipRect is not { Height: <= 0 }));
        int reads = imageReads.Count;
        long version = scene.Version;
        AssertEqual(version, fixture.Shell.Render(new(810, 600)).Version);
        AssertEqual(reads, imageReads.Count);
        fixture.Shell.Render(new(1600, 1200));
        AssertEqual(firstPreview, FindEntity(fixture.Shell, "WardrobeLibraryCard.1.Preview"));
        AssertEqual("正面 · 切换视角", LibraryText(fixture, "WardrobeLibraryCard.1.View"));
        fixture.Shell.Render(new(810, 600));
        var scrollEntity = FindEntity(fixture.Shell, "WardrobeLibraryBody");
        AssertTrue(fixture.Shell.Renderer.TryGetScrollSnapshot(scrollEntity, out var scroll));
        fixture.Shell.Tree.GetComponent<XsrUiScroll>(scrollEntity)!.OffsetY = scroll.MaximumOffsetY;
        fixture.Shell.Tree.MarkDirty(scrollEntity, XsrUiDirtyKinds.Layout);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(810, 600)); return imageReads.Any(read => read.Id == 64); }, TimeSpan.FromSeconds(5)));
        AssertTrue(imageReads[0].Stop.IsCancellationRequested);
        string fallback = fixture.Shell.Tree.GetComponent<XsrUiImage>(firstPreview)!.Raster!.Image.Key;
        imageReads[0].Reply.SetResult(XsrResult.Success(new AccountWardrobeResolvedTextures("late", null, false, WardrobeDesktopSkin(), null)));
        fixture.Shell.Render(new(810, 600));
        AssertEqual(fallback, fixture.Shell.Tree.GetComponent<XsrUiImage>(firstPreview)!.Raster!.Image.Key);
        page.Dispose();
        AssertTrue(imageReads.All(read => read.Stop.IsCancellationRequested));
    }

    private static void WardrobeLibraryNativeEffectsRequireLiveOwningSources()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Offline);
        List<Uri> opened = [];
        var queries = LibraryQueries(fixture);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(101, query.Kind)]))));
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureOpenUrl(opened.Add);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems"));
        Emit(fixture.Intents, "ui.wardrobe.library.docs");
        LibraryEmit(fixture, "ui.wardrobe.library.docs", "WardrobeLibraryOpenSite");
        var docs = FindEntity(fixture.Shell, "WardrobeLibraryDocs");
        fixture.Shell.Tree.GetComponent<XsrUiInput>(docs)!.Enabled = false;
        Emit(fixture.Intents, "ui.wardrobe.library.docs", docs); fixture.Shell.Render(new(810, 1200));
        AssertEqual(0, opened.Count);
        fixture.Shell.Tree.GetComponent<XsrUiInput>(docs)!.Enabled = true;
        var content = FindEntity(fixture.Shell, "WardrobeLibraryContent");
        fixture.Shell.Tree.GetComponent<XsrUiElement>(content)!.IsVisible = false;
        Emit(fixture.Intents, "ui.wardrobe.library.docs", docs); fixture.Shell.Render(new(810, 1200));
        AssertEqual(0, opened.Count);
        fixture.Shell.Tree.GetComponent<XsrUiElement>(content)!.IsVisible = true;
        page.ConfigureOpenUrl(_ => throw new InvalidOperationException("browser failure"));
        Emit(fixture.Intents, "ui.wardrobe.library.docs", docs); fixture.Shell.Render(new(810, 1200));
        AssertEqual("无法打开皮肤站链接，请检查默认浏览器后重试。", LibraryText(fixture, "WardrobeLibraryOperationStatus"));
        page.ConfigureOpenUrl(_ => page.Dispose());
        Emit(fixture.Intents, "ui.wardrobe.library.docs", docs); fixture.Shell.Render(new(810, 1200));
        AssertFalse(fixture.Shell.Tree.IsAlive(page.Page));
        Emit(fixture.Intents, "ui.wardrobe.library.docs", docs); fixture.Shell.Render(new(810, 1200));
        AssertEqual(0, opened.Count);
    }

    private static void WardrobeLibraryNavigationCancelsAdmittedMutation()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
        var launchPage = fixture.Shell.Stage.Navigation.Current;
        var writing = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken writeToken = default;
        int writes = 0;
        var queries = LibraryQueries(fixture);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(101, query.Kind)]))));
        var commands = new XsrCommandRouterBuilder();
        commands.Register<AccountWardrobeApplyPublicCommand>(AccountWardrobeContract.ApplyPublic, (_, token) =>
        { writes++; writeToken = token; return new(writing.Task); });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureBack(() => fixture.Shell.Stage.Navigation.Replace(launchPage));
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => LibraryEnabled(fixture, "WardrobeLibraryCard.101.Apply"));
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.101.Apply");
        LibraryPump(fixture, () => writes == 1);
        AssertEqual("正在更新外观…", LibraryText(fixture, "WardrobeLibraryOperationStatus"));
        AssertFalse(LibraryEnabled(fixture, "WardrobeLibraryRefresh"));
        var oldApply = FindEntity(fixture.Shell, "WardrobeLibraryCard.101.Apply");
        LibraryEmit(fixture, "ui.wardrobe.library.back", "WardrobeLibraryBack");
        fixture.Shell.Render(new(810, 1200)); fixture.Shell.Render(new(810, 1200));
        AssertTrue(writeToken.IsCancellationRequested);
        writing.SetResult(XsrResult.Success());
        Emit(fixture.Intents, "ui.wardrobe.library.action", oldApply); fixture.Shell.Render(new(810, 1200));
        AssertEqual(1, writes);
        AssertFalse(fixture.Shell.Tree.IsAlive(oldApply));
    }

    private static void WardrobeLibraryAdmitsOwnProfileRenameAndRejectsStaleIdentities()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Microsoft);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Bob", Kind = LaunchProfileKind.Microsoft, Uuid = "bob" }).IsSuccess);
        var delayed = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountWardrobeSnapshot? retiredSnapshot = null;
        AccountWardrobeApplyPublicCommand? applied = null;
        CancellationToken retiredToken = default;
        int reads = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(WardrobeCatalogContract.Sites,
            (_, _) => ValueTask.FromResult(XsrResult.Success(LibrarySites())));
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(401, query.Kind)]))));
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, token) =>
        {
            reads++;
            if (reads == 3)
            {
                retiredToken = token;
                retiredSnapshot = LibrarySnapshot(fixture) with { Identity = new(0, "alice", LaunchProfileKind.Microsoft, 9, 9) };
                return new(delayed.Task);
            }
            if (reads <= 2)
            {
                // The admitted provider read commits its refreshed username before replying,
                // just as Microsoft/LittleSkin authentication does in AccountWardrobeService.
                var profile = fixture.Service.GetProfile(0).Value;
                AssertTrue(fixture.Service.ReplaceProfile(0, profile with { Username = reads == 1 ? "Alice Renamed" : "Alice Again" }, profile).IsSuccess);
            }
            var snapshot = LibrarySnapshot(fixture);
            long generation = reads switch { 1 => 8, 2 => 9, 4 => 10, 5 => 9, _ => 12 };
            return ValueTask.FromResult(XsrResult.Success(snapshot with
            {
                Identity = snapshot.Identity with { RosterGeneration = generation, SelectionGeneration = reads <= 3 ? 9 : 10 }
            }));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<AccountWardrobeApplyPublicCommand>(AccountWardrobeContract.ApplyPublic, (command, _) =>
        { applied = command; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => LibraryEnabled(fixture, "WardrobeLibraryCard.401.Apply"));
        AssertEqual(1, reads); AssertEqual("Alice Renamed", fixture.Service.GetViews()[0].Username);
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 2 && LibraryEnabled(fixture, "WardrobeLibraryCard.401.Apply"));
        AssertEqual("Alice Again", fixture.Service.GetViews()[0].Username);
        var retiredApply = FindEntity(fixture.Shell, "WardrobeLibraryCard.401.Apply");
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 3 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        LibraryPump(fixture, () => reads == 4 && LibraryEnabled(fixture, "WardrobeLibraryCard.401.Apply"));
        AssertTrue(retiredToken.IsCancellationRequested);
        delayed.SetResult(XsrResult.Success(retiredSnapshot!));
        Emit(fixture.Intents, "ui.wardrobe.library.action", retiredApply); fixture.Shell.Render(new(810, 1200));
        AssertTrue(applied is null); AssertEqual(4, reads);
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 5 && LibraryVisible(fixture, "WardrobeLibraryItems"));
        AssertFalse(LibraryEnabled(fixture, "WardrobeLibraryCard.401.Apply"));
        AssertEqual("无法读取当前账户外观，请刷新重试。", LibraryText(fixture, "WardrobeLibraryAccountNotice"));
        LibraryEmit(fixture, "ui.wardrobe.library.refresh", "WardrobeLibraryRefresh");
        LibraryPump(fixture, () => reads == 6 && LibraryEnabled(fixture, "WardrobeLibraryCard.401.Apply"));
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.401.Apply");
        LibraryPump(fixture, () => applied is not null);
        AssertEqual(1, applied!.Identity.Index); AssertEqual("bob", applied.Identity.Uuid);
        AssertEqual(12L, applied.Identity.RosterGeneration); AssertEqual(10L, applied.Identity.SelectionGeneration);
    }

    private static void WardrobeLibraryKeepsOwnMutationRenameAndRetiresSelectionAba()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Microsoft);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Bob", Kind = LaunchProfileKind.Microsoft, Uuid = "bob" }).IsSuccess);
        var firstWrite = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWrite = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default, secondToken = default;
        int writes = 0, reads = 0;
        long selectionGeneration = 9;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(WardrobeCatalogContract.Sites,
            (_, _) => ValueTask.FromResult(XsrResult.Success(LibrarySites())));
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, [LibraryItem(501, query.Kind)]))));
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            reads++;
            var snapshot = LibrarySnapshot(fixture);
            return ValueTask.FromResult(XsrResult.Success(snapshot with
            { Identity = snapshot.Identity with { RosterGeneration = 7 + writes, SelectionGeneration = selectionGeneration } }));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<AccountWardrobeApplyPublicCommand>(AccountWardrobeContract.ApplyPublic, (_, token) =>
        {
            writes++;
            var profile = fixture.Service.GetProfile(0).Value;
            AssertTrue(fixture.Service.ReplaceProfile(0, profile with { Username = "Provider Name " + writes }, profile).IsSuccess);
            if (writes == 1) { firstToken = token; return new(firstWrite.Task); }
            secondToken = token; return new(secondWrite.Task);
        });
        using var page = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        LibraryPump(fixture, () => LibraryEnabled(fixture, "WardrobeLibraryCard.501.Apply"));
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.501.Apply");
        LibraryPump(fixture, () => writes == 1);
        fixture.Shell.Render(new(810, 1200)); fixture.Shell.Render(new(810, 1200));
        AssertFalse(firstToken.IsCancellationRequested); AssertEqual(1, reads);
        AssertEqual("正在更新外观…", LibraryText(fixture, "WardrobeLibraryOperationStatus"));
        firstWrite.SetResult(XsrResult.Success());
        LibraryPump(fixture, () => reads == 2 && LibraryEnabled(fixture, "WardrobeLibraryCard.501.Apply"));
        AssertEqual("Provider Name 1", fixture.Service.GetViews()[0].Username);
        AssertEqual("外观已更新。", LibraryText(fixture, "WardrobeLibraryOperationStatus"));
        LibraryEmit(fixture, "ui.wardrobe.library.action", "WardrobeLibraryCard.501.Apply");
        LibraryPump(fixture, () => writes == 2);
        fixture.Shell.Render(new(810, 1200)); AssertFalse(secondToken.IsCancellationRequested);
        var retiredApply = FindEntity(fixture.Shell, "WardrobeLibraryCard.501.Apply");
        AssertTrue(fixture.Service.SelectProfile(1) is null); AssertTrue(fixture.Service.SelectProfile(0) is null);
        selectionGeneration = 11;
        LibraryPump(fixture, () => reads == 3 && LibraryEnabled(fixture, "WardrobeLibraryCard.501.Apply"));
        AssertTrue(secondToken.IsCancellationRequested);
        secondWrite.SetResult(XsrResult.Success());
        Emit(fixture.Intents, "ui.wardrobe.library.action", retiredApply); fixture.Shell.Render(new(810, 1200));
        AssertEqual(2, writes); AssertEqual(3, reads);
        AssertFalse(LibraryVisible(fixture, "WardrobeLibraryOperationStatus"));
    }

    private static LaunchPageFixture LibraryFixture(LaunchProfileKind kind)
    {
        var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("CN"));
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Alice", Kind = kind, Uuid = "alice", AuthServer = kind == LaunchProfileKind.ThirdParty ? "https://account.example/api/yggdrasil" : "" }).IsSuccess);
        return fixture;
    }
    private static XsrQueryRouterBuilder LibraryQueries(LaunchPageFixture fixture, Action? onAccount = null)
    {
        var queries = new XsrQueryRouterBuilder();
        queries.Register<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(WardrobeCatalogContract.Sites,
            (_, _) => ValueTask.FromResult(XsrResult.Success(LibrarySites())));
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        { onAccount?.Invoke(); return ValueTask.FromResult(XsrResult.Success(LibrarySnapshot(fixture))); });
        return queries;
    }
    private static IReadOnlyList<WardrobeCatalogSite> LibrarySites() =>
        [new("littleskin", "LittleSkin", new("https://littleskin.cn/"), new("https://docs.littleskin.cn/"), "lucide/palette", true)];
    private static AccountWardrobeSnapshot LibrarySnapshot(LaunchPageFixture fixture)
    {
        var profile = fixture.Service.GetViews().Single(p => p.Index == fixture.Service.SelectedIndex);
        return new(new(profile.Index, profile.Uuid, profile.Kind, 7, 9), profile,
            profile.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin or LaunchProfileKind.NCloud,
            profile.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin, null, [])
        { ManageUri = profile.Kind == LaunchProfileKind.ThirdParty ? "https://account.example/manage" : null };
    }
    private static WardrobeCatalogItem LibraryItem(long id, WardrobeCatalogKind kind) => new(id, "搜索皮肤", "皮肤", kind == WardrobeCatalogKind.Cape ? "cape" : "alex", 42, true,
        "https://littleskin.cn/textures/" + new string('a', 64), new("https://littleskin.cn/skinlib/show/" + id), kind);
    private static WardrobeCatalogPage LibraryPage(WardrobeCatalogQuery query, IReadOnlyList<WardrobeCatalogItem> items) =>
        new(query.SiteId, "LittleSkin", "6.0.0", query.Page, query.Page > 1, true, items);
    private static void LibraryEmit(LaunchPageFixture fixture, string command, string key) => Emit(fixture.Intents, command, FindEntity(fixture.Shell, key));
    private static string LibraryText(LaunchPageFixture fixture, string key) => fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, key))!.Content;
    private static bool LibraryVisible(LaunchPageFixture fixture, string key)
    {
        var entity = FindEntity(fixture.Shell, key);
        return fixture.Shell.Tree.IsAlive(entity) && fixture.Shell.Tree.GetComponent<XsrUiElement>(entity)?.IsVisible == true;
    }
    private static bool LibraryEnabled(LaunchPageFixture fixture, string key)
    {
        var entity = FindEntity(fixture.Shell, key);
        return fixture.Shell.Tree.IsAlive(entity) && fixture.Shell.Tree.GetComponent<XsrUiInput>(entity)?.Enabled == true;
    }
    private static void LibraryPump(LaunchPageFixture fixture, Func<bool> done)
        => AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(810, 1200)); return done(); }, TimeSpan.FromSeconds(5)));
}
