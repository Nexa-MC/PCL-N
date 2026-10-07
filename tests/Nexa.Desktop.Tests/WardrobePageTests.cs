using Nexa.Core.Media;
using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WardrobePagePreviewsAndDispatchesTypedMutations()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile
        { Username = "Alice", Kind = LaunchProfileKind.Microsoft, Uuid = "0123456789abcdef0123456789abcdef", AccessToken = "PRIVATE" }).IsSuccess);
        PngImage image = WardrobeDesktopSkin();
        AccountWardrobeUploadSkinCommand? upload = null; AccountWardrobeSetCapeCommand? cape = null;
        int readCount = 0;
        string? previewPath = null;
        XsrQueryRouterBuilder queries = new();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        { readCount++; return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned"))); });
        queries.Register<AccountWardrobeSkinQuery, AccountWardrobeSkinPreview>(AccountWardrobeContract.ValidateSkin, (query, _) =>
        { previewPath = query.Path; return ValueTask.FromResult(XsrResult.Success(new AccountWardrobeSkinPreview(image, query.IsSlim, "selected.png"))); });
        XsrCommandRouterBuilder commands = new();
        commands.Register<AccountWardrobeUploadSkinCommand>(AccountWardrobeContract.UploadSkin, (command, _) =>
        { upload = command; return ValueTask.FromResult(XsrResult.Success()); });
        commands.Register<AccountWardrobeSetCapeCommand>(AccountWardrobeContract.SetCape, (command, _) =>
        { cape = command; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureFilePicker(_ => Task.FromResult<string?>("/chosen/selected.png"));
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeCapeClear"));
        int idleReads = readCount;
        var idleCape = FindEntity(fixture.Shell, "WardrobeCape.owned");
        fixture.Shell.Render(new(800, 600)); fixture.Shell.Render(new(800, 600));
        AssertEqual(idleReads, readCount); AssertEqual(idleCape, FindEntity(fixture.Shell, "WardrobeCape.owned"));
        AssertFalse(WardrobeText(fixture, "WardrobeStatus").Contains("迁移", StringComparison.Ordinal));
        WardrobeEmit(fixture, "ui.wardrobe.browse", "WardrobeBrowse");
        WardrobePump(fixture, () => previewPath is not null);
        WardrobePump(fixture, () => fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeTexturePreview"))!.Raster is not null);
        AssertEqual("/chosen/selected.png", previewPath);
        var head = fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeHeadPreview"))!.Raster!;
        AssertEqual(2, head.Layers.Count); AssertEqual(image.Key, head.Image.Key);
        WardrobeEmit(fixture, "ui.wardrobe.upload", "WardrobeUpload"); WardrobePump(fixture, () => upload is not null);
        AssertTrue(upload!.PngBytes.AsSpan().SequenceEqual(image.Bytes.Span)); AssertFalse(upload.IsSlim);
        WardrobePump(fixture, () => !WardrobeText(fixture, "WardrobeStatus").Contains("正在", StringComparison.Ordinal));
        AssertEqual(idleReads + 1, readCount);
        Emit(fixture.Intents, "ui.wardrobe.cape.choose", FindEntity(fixture.Shell, "WardrobeCape.owned"));
        WardrobePump(fixture, () => cape is not null); AssertEqual("owned", cape!.CapeId);
        WardrobePump(fixture, () => !WardrobeText(fixture, "WardrobeStatus").Contains("正在", StringComparison.Ordinal));
        AssertEqual(idleReads + 2, readCount);
        cape = null; WardrobeEmit(fixture, "ui.wardrobe.cape.clear", "WardrobeCapeClear");
        WardrobePump(fixture, () => cape is not null); AssertTrue(cape!.CapeId is null);
        AssertFalse(fixture.Shell.Render(new(800, 600)).Nodes.Any(n => n.Text?.Contains("PRIVATE", StringComparison.Ordinal) == true));
    }
    private static void WardrobePageRetiresLateQueriesPickersAndPreviews()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        foreach (string name in new[] { "Alice", "Bob" })
            AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = name, Kind = LaunchProfileKind.Microsoft, Uuid = name }).IsSuccess);
        var first = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validation = new TaskCompletionSource<XsrResult<AccountWardrobeSkinPreview>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var texture = new TaskCompletionSource<XsrResult<AccountWardrobeResolvedTextures>>(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountWardrobeSnapshot old = WardrobeDesktopSnapshot(fixture, "Old cape");
        int reads = 0, previews = 0, textures = 0; CancellationToken firstToken = default, pickerToken = default, previewToken = default, textureToken = default;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1) { firstToken = token; return new(first.Task); }
            return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Current cape")));
        });
        queries.Register<AccountWardrobeSkinQuery, AccountWardrobeSkinPreview>(AccountWardrobeContract.ValidateSkin, (_, token) =>
        { previews++; previewToken = token; return new(validation.Task); });
        queries.Register<AccountWardrobeTextureQuery, AccountWardrobeResolvedTextures>(AccountWardrobeContract.ReadTexture, (_, token) =>
        {
            if (++textures == 1) { textureToken = token; return new(texture.Task); }
            return ValueTask.FromResult(XsrResult.Success(new AccountWardrobeResolvedTextures(null, null, false, null, null)));
        });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureFilePicker(token => { pickerToken = token; return picker.Task; });
        var launchPage = fixture.Shell.Stage.Navigation.Current;
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Render(new(800, 600));
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity") == "Bob" && WardrobeEnabled(fixture, "WardrobeBrowse") && textures > 0);
        AssertTrue(firstToken.IsCancellationRequested);
        first.SetResult(XsrResult.Success(old)); fixture.Shell.Render(new(800, 600));
        AssertFalse(fixture.Shell.Render(new(800, 600)).Nodes.Any(n => n.Text?.Contains("Old cape", StringComparison.Ordinal) == true));
        WardrobeEmit(fixture, "ui.wardrobe.browse", "WardrobeBrowse"); fixture.Shell.Render(new(800, 600));
        AssertTrue(fixture.Service.SelectProfile(0) is null);
        WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeCapeClear"));
        AssertTrue(pickerToken.IsCancellationRequested); AssertTrue(textureToken.IsCancellationRequested); picker.SetResult("/late/skin.png");
        fixture.Shell.Render(new(800, 600)); AssertEqual(0, previews);
        AssertEqual("", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(FindEntity(fixture.Shell, "WardrobeSkinPath"))!.ReadDraft());
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeTexturePreview"))!.Raster is null);
        var activeCape = FindEntity(fixture.Shell, "WardrobeCapePreview.owned");
        var stableCape = fixture.Shell.Tree.GetComponent<XsrUiImage>(activeCape)!.Raster;
        texture.SetResult(XsrResult.Success(new AccountWardrobeResolvedTextures(null, null, true, WardrobeDesktopSkin(), null)));
        fixture.Shell.Render(new(800, 600));
        AssertTrue(ReferenceEquals(stableCape, fixture.Shell.Tree.GetComponent<XsrUiImage>(activeCape)!.Raster));
        page.ConfigureFilePicker(_ => Task.FromResult<string?>("/selected/skin.png"));
        WardrobeEmit(fixture, "ui.wardrobe.browse", "WardrobeBrowse"); WardrobePump(fixture, () => previews == 1);
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity") == "Bob" && WardrobeEnabled(fixture, "WardrobeBrowse"));
        AssertTrue(previewToken.IsCancellationRequested);
        validation.SetResult(XsrResult.Success(new AccountWardrobeSkinPreview(WardrobeDesktopSkin(), true, "late.png")));
        fixture.Shell.Render(new(800, 600));
        AssertFalse(WardrobeVisible(fixture, "WardrobeLocalLayer"));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeTexturePreview"))!.Raster is null);
        var retiredHead = FindEntity(fixture.Shell, "WardrobeHeadPreview");
        fixture.Shell.Stage.Navigation.Replace(launchPage);
        fixture.Shell.Render(new(800, 600));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(retiredHead)!.Raster is null);
    }
    private static AccountWardrobeSnapshot WardrobeDesktopSnapshot(LaunchPageFixture fixture, string capeName)
    {
        var view = fixture.Service.GetViews().Single(p => p.Index == fixture.Service.SelectedIndex);
        return new(new(view.Index, view.Uuid, view.Kind, 1, 1), view, true, true, null,
            [new("owned", capeName, "https://textures.minecraft.net/cape", false)]);
    }
    private static void WardrobePageRefreshesAdmissionAfterFailedMutation()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = "Alice", Kind = LaunchProfileKind.Microsoft, Uuid = "alice" }).IsSuccess);
        int reads = 0, attempts = 0; long secondStamp = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            var snapshot = WardrobeDesktopSnapshot(fixture, "Cape");
            return ValueTask.FromResult(XsrResult.Success(snapshot with { Identity = snapshot.Identity with { RosterGeneration = ++reads } }));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<AccountWardrobeSetCapeCommand>(AccountWardrobeContract.SetCape, (command, _) =>
        {
            if (++attempts == 1)
            {
                fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with { AccessToken = "ROTATED" });
                return ValueTask.FromResult(XsrResult.Failure(new XsrError(XsrErrorKind.Rejected,
                    XsrSemanticId.Parse("fixture.provider_failure"), "Provider failure")));
            }
            secondStamp = command.Identity.RosterGeneration;
            return ValueTask.FromResult(XsrResult.Success());
        });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeCapeClear"));
        WardrobeEmit(fixture, "ui.wardrobe.cape.clear", "WardrobeCapeClear");
        WardrobePump(fixture, () => reads >= 2 && fixture.Shell.Tree.GetComponent<XsrUiInput>(FindEntity(fixture.Shell, "WardrobeCapeClear"))!.Enabled);
        AssertEqual("Provider failure", WardrobeText(fixture, "WardrobeStatus"));
        WardrobeEmit(fixture, "ui.wardrobe.cape.clear", "WardrobeCapeClear"); WardrobePump(fixture, () => attempts == 2);
        AssertEqual(2L, secondStamp);
        WardrobePump(fixture, () => reads >= 3 && fixture.Shell.Tree.GetComponent<XsrUiInput>(FindEntity(fixture.Shell, "WardrobeCapeClear"))!.Enabled);
        AssertEqual(3, reads);
        page.Dispose();
        int failedReads = 0;
        var failureQueries = new XsrQueryRouterBuilder();
        failureQueries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            failedReads++;
            fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with { AccessToken = "READ-ROTATED" });
            return ValueTask.FromResult(XsrResult.Failure<AccountWardrobeSnapshot>(new XsrError(XsrErrorKind.Rejected,
                XsrSemanticId.Parse("fixture.cape_read_failure"), "Cape read failure")));
        });
        using var failedPage = new WardrobePageController(fixture.Shell, fixture.Intents,
            failureQueries.Build(new NoopDispatchObserver()), new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()),
            fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(failedPage.Page);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeStatus") == "Cape read failure");
        for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
        AssertEqual(1, failedReads);
    }
    private static void WardrobePagePaginatesCapeInventory()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        fixture.Service.AddProfile(new LaunchProfile { Username = "Alice", Kind = LaunchProfileKind.Microsoft, Uuid = "alice" });
        int textureReads = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            var snapshot = WardrobeDesktopSnapshot(fixture, "Cape");
            return ValueTask.FromResult(XsrResult.Success(snapshot with
            {
                Capes = Enumerable.Range(0, 30)
                .Select(i => new AccountWardrobeCape(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Cape " + i,
                    "https://textures.minecraft.net/cape", false)).ToArray()
            }));
        });
        queries.Register<AccountWardrobeTextureQuery, AccountWardrobeResolvedTextures>(AccountWardrobeContract.ReadTexture, (_, _) =>
        { textureReads++; return ValueTask.FromResult(XsrResult.Success(new AccountWardrobeResolvedTextures(null, null, false, WardrobeDesktopSkin(), null))); });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        var list = FindEntity(fixture.Shell, "WardrobeCapes");
        WardrobePump(fixture, () => textureReads == WardrobePageController.CapePageSize);
        // dev retains the full horizontal inventory; only its image window is bounded.
        AssertEqual(30, fixture.Shell.Tree.Children(list).Count);
        AssertEqual("30 项", WardrobeText(fixture, "WardrobeCapeCount"));
        var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(list)!;
        AssertTrue(scroll.UseVerticalWheelForHorizontalScroll);
        AssertTrue(fixture.Shell.Renderer.TryGetScrollSnapshot(list, out var track));
        double maximumOffset = Math.Max(0, track.ContentWidth - track.ViewportWidth);
        AssertTrue(maximumOffset > 0);
        var first = FindEntity(fixture.Shell, "WardrobeCapePreview.0");
        var stable = fixture.Shell.Tree.GetComponent<XsrUiImage>(first)!.Raster;
        for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
        AssertEqual(WardrobePageController.CapePageSize, textureReads);
        AssertTrue(ReferenceEquals(stable, fixture.Shell.Tree.GetComponent<XsrUiImage>(first)!.Raster));
        WardrobeEmit(fixture, "ui.wardrobe.cape.next", "WardrobeCapeNext"); fixture.Shell.Render(new(800, 600));
        AssertTrue(scroll.OffsetX > 0); AssertEqual(30, fixture.Shell.Tree.Children(list).Count);
        for (int step = 0; step < 30 && WardrobeEnabled(fixture, "WardrobeCapeNext"); step++)
        { WardrobeEmit(fixture, "ui.wardrobe.cape.next", "WardrobeCapeNext"); fixture.Shell.Render(new(800, 600)); }
        AssertEqual(maximumOffset, scroll.OffsetX);
        // Pager enablement observes the renderer's final clamped offset on the following frame.
        fixture.Shell.Render(new(800, 600));
        AssertFalse(WardrobeEnabled(fixture, "WardrobeCapeNext"));
        WardrobeEmit(fixture, "ui.wardrobe.cape.previous", "WardrobeCapePrevious"); fixture.Shell.Render(new(800, 600));
        AssertTrue(scroll.OffsetX < maximumOffset);
    }

    private static void WardrobePageMatchesDevTracksAndResponsivePlayerRail()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Microsoft);
        PngImage image = WardrobeDesktopSkin();
        var current = new AccountWardrobeResolvedTextures("https://textures.minecraft.net/current", null, true, image, null);
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
            ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned") with
            {
                Current = current,
                Skins = [new("history", "搜索皮肤", "此前使用", current, true), new("other", "Bob", "其他档案", current, true)]
            })));
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureFilePicker(_ => Task.FromResult<string?>(null));
        int libraries = 0; page.ConfigureLibrary(() => libraries++);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeSkinCount") == "2 项");
        AssertEqual(268d, WardrobeOuterWidth(fixture, "WardrobeProfileRail"));
        AssertEqual(XsrUiOrientation.Horizontal, fixture.Shell.Tree.GetComponent<XsrUiStackPanel>(FindEntity(fixture.Shell, "WardrobeSkins"))!.Direction);
        AssertEqual(XsrUiOrientation.Horizontal, fixture.Shell.Tree.GetComponent<XsrUiStackPanel>(FindEntity(fixture.Shell, "WardrobeCapes"))!.Direction);
        AssertEqual("此前使用", WardrobeText(fixture, "WardrobeSkinSource.history"));
        AssertEqual("其他档案", WardrobeText(fixture, "WardrobeSkinSource.other"));
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, "WardrobeSkinTitle.history"))!.Localize);
        var scene = fixture.Shell.Render(new(800, 600));
        var preview = scene.Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobePlayerPreview"));
        AssertTrue(preview.Rect.Width > 100 && preview.Rect.Height >= 80);
        AssertTrue(preview.RasterImage!.Layers.Count > 2); AssertEqual(.65, preview.RasterImage.AspectRatio);
        var historyCard = scene.Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobeSkinCard.history"));
        var historyApply = scene.Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobeSkinApply.history"));
        AssertContains(historyCard.Rect, historyApply.Rect);
        foreach (string key in new[] { "WardrobeBrowse", "WardrobeLibrary" })
        {
            var node = scene.Nodes.Single(item => item.Entity == FindEntity(fixture.Shell, key));
            AssertTrue(node.Rect.Y + node.Rect.Height <= 600); AssertTrue(node.Rect.Width > 100);
        }
        var recipe = preview.RasterImage;
        for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
        AssertTrue(ReferenceEquals(recipe, fixture.Shell.Tree.GetComponent<XsrUiImage>(preview.Entity)!.Raster));
        string[] views = ["背面", "左侧", "右侧", "顶部", "底部", "立体", "正面"];
        foreach (string view in views)
        {
            WardrobeEmit(fixture, "ui.wardrobe.view.current", "WardrobeCurrentView"); fixture.Shell.Render(new(800, 600));
            AssertEqual(view + " · 切换视角", WardrobeText(fixture, "WardrobeCurrentView"));
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(preview.Entity)!.Raster!.Layers.Count > 0);
        }
        WardrobeEmit(fixture, "ui.wardrobe.view.card", "WardrobeSkinView.history"); fixture.Shell.Render(new(800, 600));
        AssertEqual("背面 · 切换视角", WardrobeText(fixture, "WardrobeSkinView.history"));
        WardrobeEmit(fixture, "ui.wardrobe.library", "WardrobeLibrary"); fixture.Shell.Render(new(800, 600));
        AssertEqual(1, libraries); AssertEqual(FindEntity(fixture.Shell, "WardrobeLibrary"), page.LibraryButton);
        double contentWidth = scene.Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobeContent")).Rect.Width;
        WardrobeEmit(fixture, "ui.wardrobe.browse", "WardrobeBrowse"); fixture.Shell.Render(new(800, 600));
        AssertTrue(WardrobeVisible(fixture, "WardrobeLocalLayer"));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiOverlayLayer>(FindEntity(fixture.Shell, "WardrobeLocalLayer"))!.IsModal);
        AssertEqual(contentWidth, fixture.Shell.Render(new(800, 600)).Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobeContent")).Rect.Width);
        WardrobeEmit(fixture, "ui.wardrobe.library", "WardrobeLibrary"); fixture.Shell.Render(new(800, 600)); AssertEqual(1, libraries);
        WardrobeEmit(fixture, "ui.wardrobe.local.close", "WardrobeLocalClose"); fixture.Shell.Render(new(800, 600));
        AssertFalse(WardrobeVisible(fixture, "WardrobeLocalLayer"));
        fixture.Shell.Render(new(1600, 1000));
        AssertEqual(326d, WardrobeOuterWidth(fixture, "WardrobeProfileRail"));
        fixture.Shell.Render(new(1200, 800));
        AssertEqual(300d, WardrobeOuterWidth(fixture, "WardrobeProfileRail"));
        fixture.Shell.Render(new(800, 400));
        var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(FindEntity(fixture.Shell, "WardrobeContent"))!;
        AssertTrue(fixture.Shell.Renderer.TryGetScrollSnapshot(FindEntity(fixture.Shell, "WardrobeContent"), out var contentScroll));
        AssertTrue(contentScroll.MaximumOffsetY > 0);
        scroll.OffsetY = contentScroll.MaximumOffsetY;
        fixture.Shell.Tree.MarkDirty(FindEntity(fixture.Shell, "WardrobeContent"), XsrUiDirtyKinds.Layout);
        var low = fixture.Shell.Render(new(800, 400));
        var clear = low.Nodes.Single(node => node.Entity == FindEntity(fixture.Shell, "WardrobeCapeClear"));
        AssertTrue(clear.Rect.Y + clear.Rect.Height <= 400);
    }

    private static void WardrobePageRejectsForeignDisabledAndRetiredSources()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Microsoft);
        int applies = 0, reads = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            reads++;
            return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned") with
            { Skins = [new("history", "Alice", "此前使用", new(null, null, false, null, null), true)] }));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<AccountWardrobeApplyCardCommand>(AccountWardrobeContract.ApplyCard, (_, _) =>
        { applies++; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeSkinCount") == "1 项");
        var source = FindEntity(fixture.Shell, "WardrobeSkinApply.history");
        Emit(fixture.Intents, "ui.wardrobe.skin.choose");
        WardrobeEmit(fixture, "ui.wardrobe.skin.choose", "WardrobeCurrentView");
        fixture.Shell.Render(new(800, 600)); AssertEqual(0, applies);
        fixture.Shell.Tree.GetComponent<XsrUiInput>(source)!.Enabled = false;
        Emit(fixture.Intents, "ui.wardrobe.skin.choose", source); fixture.Shell.Render(new(800, 600)); AssertEqual(0, applies);
        fixture.Shell.Tree.GetComponent<XsrUiInput>(source)!.Enabled = true;
        fixture.Shell.Tree.SetComponent(source, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.wardrobe.cape.choose")));
        Emit(fixture.Intents, "ui.wardrobe.skin.choose", source); fixture.Shell.Render(new(800, 600)); AssertEqual(0, applies);
        fixture.Shell.Tree.SetComponent(source, new XsrUiCommandBinding(XsrSemanticId.Parse("ui.wardrobe.refresh")));
        Emit(fixture.Intents, "ui.wardrobe.refresh", source); fixture.Shell.Render(new(800, 600));
        AssertEqual(1, reads); AssertTrue(fixture.Shell.Tree.IsAlive(source));
        WardrobeEmit(fixture, "ui.wardrobe.refresh", "WardrobeRefresh");
        WardrobePump(fixture, () => !fixture.Shell.Tree.IsAlive(source) && WardrobeText(fixture, "WardrobeSkinCount") == "1 项");
        Emit(fixture.Intents, "ui.wardrobe.skin.choose", source); fixture.Shell.Render(new(800, 600)); AssertEqual(0, applies);
        WardrobeEmit(fixture, "ui.wardrobe.skin.choose", "WardrobeSkinApply.history");
        WardrobePump(fixture, () => applies == 1);
    }

    private static void WardrobePageSeparatesReadFailureFromEmptyAndAdmitsBrowserHandoff()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.ThirdParty);
        var delayed = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
        {
            if (++reads == 1) return new(delayed.Task);
            var snapshot = WardrobeDesktopSnapshot(fixture, "") with
            {
                CanUploadSkin = false,
                CanChooseCape = false,
                Capes = [],
                CapeState = AccountWardrobeCapeState.Unsupported,
                UnavailableReason = "第三方皮肤站需要独立授权。",
                ManageUri = "https://account.example/user"
            };
            return ValueTask.FromResult(XsrResult.Success(snapshot));
        });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        List<Uri> opened = []; page.ConfigureOpenUrl(opened.Add);
        fixture.Shell.Stage.Navigation.Replace(page.Page); WardrobePump(fixture, () => reads == 1);
        AssertEqual("正在读取可展示的皮肤…", WardrobeText(fixture, "WardrobeSkinsEmpty"));
        AssertEqual("正在读取当前账户的披风…", WardrobeText(fixture, "WardrobeCapeStatus"));
        delayed.SetResult(XsrResult.Failure<AccountWardrobeSnapshot>(new XsrError(XsrErrorKind.Rejected,
            XsrSemanticId.Parse("fixture.wardrobe_failure"), "读取失败")));
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeStatus") == "读取失败");
        AssertTrue(WardrobeText(fixture, "WardrobeSkinsEmpty").Contains("暂时无法读取", StringComparison.Ordinal));
        AssertTrue(WardrobeText(fixture, "WardrobeCapeStatus").Contains("暂时无法读取", StringComparison.Ordinal));
        for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600)); AssertEqual(1, reads);
        WardrobeEmit(fixture, "ui.wardrobe.refresh", "WardrobeRefresh");
        WardrobePump(fixture, () => WardrobeVisible(fixture, "WardrobeManage"));
        AssertTrue(WardrobeText(fixture, "WardrobeSkinsEmpty").Contains("还没有发现", StringComparison.Ordinal));
        AssertTrue(WardrobeText(fixture, "WardrobeCapeStatus").Contains("独立授权", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.wardrobe.manage"); fixture.Shell.Render(new(800, 600)); AssertEqual(0, opened.Count);
        WardrobeEmit(fixture, "ui.wardrobe.manage", "WardrobeManage"); fixture.Shell.Render(new(800, 600));
        WardrobeEmit(fixture, "ui.wardrobe.browse", "WardrobeBrowse"); fixture.Shell.Render(new(800, 600));
        AssertEqual(2, opened.Count); AssertEqual("https://account.example/user", opened[0].AbsoluteUri);
        AssertFalse(WardrobeVisible(fixture, "WardrobeLocalLayer"));
    }
    private static void WardrobePageConsumesAdmittedRenameWithoutRetryAndRejectsSelectionAba()
    {
        foreach (bool providerFails in new[] { false, true })
        {
            using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
            int reads = 0;
            var queries = new XsrQueryRouterBuilder();
            queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
            {
                if (++reads == 1)
                {
                    AssertTrue(fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with
                    { Username = "ProviderName", AccessToken = "ROTATED" }).IsSuccess);
                    if (providerFails) return ValueTask.FromResult(XsrResult.Failure<AccountWardrobeSnapshot>(new XsrError(
                        XsrErrorKind.Rejected, XsrSemanticId.Parse("fixture.rename_read_failure"), "提供方读取失败")));
                }
                return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned")));
            });
            using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
            fixture.Shell.Stage.Navigation.Replace(page.Page);
            WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity") == "ProviderName"
                && (providerFails ? WardrobeText(fixture, "WardrobeStatus") == "提供方读取失败" : WardrobeEnabled(fixture, "WardrobeCapeClear")));
            for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
            AssertEqual(1, reads);
            if (providerFails)
            {
                AssertFalse(WardrobeEnabled(fixture, "WardrobeCapeClear"));
                WardrobeEmit(fixture, "ui.wardrobe.refresh", "WardrobeRefresh");
                WardrobePump(fixture, () => reads == 2 && WardrobeEnabled(fixture, "WardrobeCapeClear"));
                AssertEqual("ProviderName", WardrobeText(fixture, "WardrobeIdentity"));
            }
        }
        foreach (bool removeAndReadd in new[] { false, true })
        {
            using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
            if (!removeAndReadd) AssertTrue(fixture.Service.AddProfile(new LaunchProfile
            { Username = "Bob", Kind = LaunchProfileKind.LittleSkin, Uuid = "bob" }).IsSuccess);
            var delayed = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken capturedToken = default;
            AccountWardrobeSnapshot old = WardrobeDesktopSnapshot(fixture, "Old cape");
            int reads = 0;
            var queries = new XsrQueryRouterBuilder();
            queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, token) =>
            {
                if (++reads == 1)
                {
                    capturedToken = token;
                    AssertTrue(fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with { Username = "ProviderName" }).IsSuccess);
                    return new(delayed.Task);
                }
                return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Current cape")));
            });
            using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
            fixture.Shell.Stage.Navigation.Replace(page.Page);
            WardrobePump(fixture, () => reads == 1 && WardrobeText(fixture, "WardrobeIdentity") == "ProviderName");
            for (int frame = 0; frame < 5; frame++) fixture.Shell.Render(new(800, 600));
            AssertEqual(1, reads); AssertFalse(capturedToken.IsCancellationRequested);
            if (removeAndReadd)
            {
                var samePrincipal = fixture.Service.GetProfile(0).Value;
                AssertTrue(fixture.Service.RemoveProfile(0).IsSuccess);
                AssertTrue(fixture.Service.AddProfile(samePrincipal).IsSuccess);
            }
            else
            { AssertTrue(fixture.Service.SelectProfile(1) is null); AssertTrue(fixture.Service.SelectProfile(0) is null); }
            WardrobePump(fixture, () => reads == 2 && WardrobeEnabled(fixture, "WardrobeCapeClear"));
            AssertTrue(capturedToken.IsCancellationRequested);
            delayed.SetResult(XsrResult.Success(old)); fixture.Shell.Render(new(800, 600));
            AssertEqual("ProviderName", WardrobeText(fixture, "WardrobeIdentity"));
            AssertFalse(fixture.Shell.Render(new(800, 600)).Nodes.Any(node => node.Text == "Old cape"));
            AssertEqual("Current cape", WardrobeText(fixture, "WardrobeCapeTitle.owned"));
        }
    }

    private static void WardrobePageKeepsAdmittedWriteRenamePendingAndRetiresAba()
    {
        foreach (bool providerFails in new[] { false, true })
        {
            using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
            var completed = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken writeToken = default;
            int reads = 0, writes = 0;
            var queries = new XsrQueryRouterBuilder();
            queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
            {
                reads++;
                return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned")));
            });
            var commands = new XsrCommandRouterBuilder();
            commands.Register<AccountWardrobeSetCapeCommand>(AccountWardrobeContract.SetCape, (command, token) =>
            {
                writes++; writeToken = token;
                AssertEqual("alice", command.Identity.Uuid);
                AssertTrue(fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with
                { Username = "ProviderName", AccessToken = "ROTATED" }).IsSuccess);
                return new(completed.Task);
            });
            using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
            fixture.Shell.Stage.Navigation.Replace(page.Page);
            WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeCapeClear"));
            WardrobeEmit(fixture, "ui.wardrobe.cape.clear", "WardrobeCapeClear");
            WardrobePump(fixture, () => writes == 1 && WardrobeText(fixture, "WardrobeIdentity") == "ProviderName");
            for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
            AssertFalse(writeToken.IsCancellationRequested); AssertEqual(1, reads);
            AssertEqual("正在更新外观…", WardrobeText(fixture, "WardrobeStatus"));
            AssertFalse(WardrobeEnabled(fixture, "WardrobeCapeClear"));
            AssertTrue(WardrobeVisible(fixture, "WardrobeCancel"));
            completed.SetResult(providerFails ? XsrResult.Failure(new XsrError(XsrErrorKind.Rejected,
                XsrSemanticId.Parse("fixture.rename_write_failure"), "提供方更新失败")) : XsrResult.Success());
            WardrobePump(fixture, () => reads == 2 && WardrobeEnabled(fixture, "WardrobeCapeClear"));
            AssertFalse(writeToken.IsCancellationRequested); AssertEqual("ProviderName", WardrobeText(fixture, "WardrobeIdentity"));
            AssertEqual(providerFails ? "提供方更新失败" : "选择皮肤或已获得的披风。", WardrobeText(fixture, "WardrobeStatus"));
            for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
            AssertEqual(2, reads); AssertEqual(1, writes);
        }
        foreach (bool removeAndReadd in new[] { false, true })
        {
            using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.LittleSkin);
            if (!removeAndReadd) AssertTrue(fixture.Service.AddProfile(new LaunchProfile
            { Username = "Bob", Kind = LaunchProfileKind.LittleSkin, Uuid = "bob" }).IsSuccess);
            var completed = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken writeToken = default;
            int reads = 0, writes = 0;
            var queries = new XsrQueryRouterBuilder();
            queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
            {
                reads++;
                return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Owned")));
            });
            var commands = new XsrCommandRouterBuilder();
            commands.Register<AccountWardrobeSetCapeCommand>(AccountWardrobeContract.SetCape, (_, token) =>
            {
                writes++; writeToken = token;
                AssertTrue(fixture.Service.ReplaceProfile(0, fixture.Service.GetProfile(0).Value with
                { Username = "ProviderName", AccessToken = "ROTATED" }).IsSuccess);
                return new(completed.Task);
            });
            using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
            fixture.Shell.Stage.Navigation.Replace(page.Page);
            WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeCapeClear"));
            WardrobeEmit(fixture, "ui.wardrobe.cape.clear", "WardrobeCapeClear");
            WardrobePump(fixture, () => writes == 1 && WardrobeText(fixture, "WardrobeIdentity") == "ProviderName");
            AssertFalse(writeToken.IsCancellationRequested);
            if (removeAndReadd)
            {
                var samePrincipal = fixture.Service.GetProfile(0).Value;
                AssertTrue(fixture.Service.RemoveProfile(0).IsSuccess);
                AssertTrue(fixture.Service.AddProfile(samePrincipal).IsSuccess);
            }
            else
            { AssertTrue(fixture.Service.SelectProfile(1) is null); AssertTrue(fixture.Service.SelectProfile(0) is null); }
            WardrobePump(fixture, () => reads == 2 && WardrobeEnabled(fixture, "WardrobeCapeClear"));
            AssertTrue(writeToken.IsCancellationRequested);
            completed.SetResult(XsrResult.Success());
            for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(800, 600));
            AssertEqual(2, reads); AssertEqual(1, writes);
            AssertEqual("ProviderName", WardrobeText(fixture, "WardrobeIdentity"));
            AssertEqual("选择皮肤或已获得的披风。", WardrobeText(fixture, "WardrobeStatus"));
            AssertFalse(fixture.Shell.Render(new(800, 600)).Nodes.Any(node => node.Text == "外观已更新。"));
        }
    }

    private static void WardrobePageLibraryNavigationRestoresTitlesAndFocus()
    {
        using LaunchPageFixture fixture = LibraryFixture(LaunchProfileKind.Microsoft);
        var queries = LibraryQueries(fixture);
        queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
            ValueTask.FromResult(XsrResult.Success(LibraryPage(query, []))));
        var router = queries.Build(new NoopDispatchObserver());
        var commands = new XsrCommandRouterBuilder().Build(new NoopDispatchObserver());
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, router, commands, fixture.Store, fixture.Feedback);
        using var library = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, router, commands, fixture.Store, fixture.Feedback);
        fixture.Controller.WardrobePage = page.Page;
        page.ConfigureLibrary(() => fixture.Controller.OpenWardrobeLibraryPage(library.Page, page.LibraryButton));
        library.ConfigureBack(() => Emit(fixture.Intents, "ui.page.back"));
        foreach (bool keyboard in new[] { true, false })
        {
            var home = fixture.Shell.Render(new(800, 600));
            var wardrobe = FindByKey(fixture.Shell, home, "AccountWardrobe").Entity;
            AssertTrue(fixture.Shell.Renderer.Focus(wardrobe, keyboard));
            AssertTrue(keyboard ? fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter) : fixture.Shell.Renderer.Activate(wardrobe));
            WardrobePump(fixture, () => WardrobeEnabled(fixture, "WardrobeLibrary"));
            AssertEqual(2, fixture.Shell.Stage.Navigation.Depth);
            AssertEqual("更衣橱", FindByKey(fixture.Shell, fixture.Shell.Render(new(800, 600)), "TitleSubpage").Text);
            AssertTrue(fixture.Shell.Renderer.Focus(page.LibraryButton, keyboard));
            AssertTrue(keyboard ? fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter) : fixture.Shell.Renderer.Activate(page.LibraryButton));
            WardrobePump(fixture, () => fixture.Shell.Stage.Navigation.Current == library.Page);
            var catalogue = fixture.Shell.Render(new(800, 600));
            AssertEqual(3, fixture.Shell.Stage.Navigation.Depth);
            AssertEqual("皮肤库", FindByKey(fixture.Shell, catalogue, "TitleSubpage").Text);
            AssertEqual(keyboard, FindByKey(fixture.Shell, catalogue, "TitleBack").IsFocusVisible);
            var back = FindByKey(fixture.Shell, catalogue, keyboard ? "TitleBack" : "WardrobeLibraryBack").Entity;
            AssertTrue(fixture.Shell.Renderer.Focus(back, keyboard));
            AssertTrue(keyboard ? fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter) : fixture.Shell.Renderer.Activate(back));
            WardrobePump(fixture, () => fixture.Shell.Stage.Navigation.Current == page.Page && WardrobeEnabled(fixture, "WardrobeLibrary"));
            var restored = fixture.Shell.Render(new(800, 600));
            AssertEqual(2, fixture.Shell.Stage.Navigation.Depth); AssertEqual("更衣橱", FindByKey(fixture.Shell, restored, "TitleSubpage").Text);
            AssertEqual(page.LibraryButton, fixture.Shell.Renderer.Focused);
            AssertEqual(keyboard, FindByKey(fixture.Shell, restored, "WardrobeLibrary").IsFocusVisible);
            var titleBack = FindByKey(fixture.Shell, restored, "TitleBack").Entity;
            AssertTrue(fixture.Shell.Renderer.Focus(titleBack, keyboard));
            AssertTrue(keyboard ? fixture.Shell.Renderer.HandleKey(XsrUiKey.Enter) : fixture.Shell.Renderer.Activate(titleBack));
            var launch = fixture.Shell.Render(new(800, 600));
            AssertEqual(1, fixture.Shell.Stage.Navigation.Depth); AssertTrue(HasKey(fixture.Shell, launch, "LaunchPage"));
            AssertEqual(wardrobe, fixture.Shell.Renderer.Focused);
            AssertEqual(keyboard, FindByKey(fixture.Shell, launch, "AccountWardrobe").IsFocusVisible);
        }
    }

    private static PngImage WardrobeDesktopSkin()
    {
        using var resource = typeof(Nexa.UI.Next.Backend.Avalonia.AvaloniaUiShellHost).Assembly
            .GetManifestResourceStream("Nexa.UI.Next.Backend.Avalonia.Assets.Avatars.Steve.png")!;
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return PngImage.TryCreate(buffer.ToArray())!;
    }
    private static string WardrobeText(LaunchPageFixture fixture, string key)
        => fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, key))!.Content;
    private static double WardrobeOuterWidth(LaunchPageFixture fixture, string key)
    {
        var element = fixture.Shell.Tree.GetComponent<XsrUiElement>(FindEntity(fixture.Shell, key))!;
        return element.Width!.Value + element.Padding.Horizontal;
    }
    private static void WardrobeEmit(LaunchPageFixture fixture, string command, string key) => Emit(fixture.Intents, command, FindEntity(fixture.Shell, key));
    private static bool WardrobeEnabled(LaunchPageFixture fixture, string key) => fixture.Shell.Tree.GetComponent<XsrUiInput>(FindEntity(fixture.Shell, key))?.Enabled == true;
    private static bool WardrobeVisible(LaunchPageFixture fixture, string key) => fixture.Shell.Tree.GetComponent<XsrUiElement>(FindEntity(fixture.Shell, key))?.IsVisible == true;
    private static void WardrobePump(LaunchPageFixture fixture, Func<bool> done)
    {
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(800, 600)); return done(); }, TimeSpan.FromSeconds(5)));
    }
}
