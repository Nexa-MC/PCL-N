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
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity").Contains("Alice", StringComparison.Ordinal));
        int idleReads = readCount;
        var idleCape = FindEntity(fixture.Shell, "WardrobeCape.owned");
        fixture.Shell.Render(new(810, 2400)); fixture.Shell.Render(new(810, 2400));
        AssertEqual(idleReads, readCount); AssertEqual(idleCape, FindEntity(fixture.Shell, "WardrobeCape.owned"));
        AssertFalse(WardrobeText(fixture, "WardrobeStatus").Contains("迁移", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.wardrobe.browse");
        WardrobePump(fixture, () => previewPath is not null);
        WardrobePump(fixture, () => fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeTexturePreview"))!.Raster is not null);
        AssertEqual("/chosen/selected.png", previewPath);
        var head = fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeHeadPreview"))!.Raster!;
        AssertEqual(2, head.Layers.Count); AssertEqual(image.Key, head.Image.Key);
        Emit(fixture.Intents, "ui.wardrobe.upload"); WardrobePump(fixture, () => upload is not null);
        AssertTrue(upload!.PngBytes.AsSpan().SequenceEqual(image.Bytes.Span)); AssertFalse(upload.IsSlim);
        WardrobePump(fixture, () => !WardrobeText(fixture, "WardrobeStatus").Contains("正在", StringComparison.Ordinal));
        AssertEqual(idleReads + 1, readCount);
        Emit(fixture.Intents, "ui.wardrobe.cape.choose", FindEntity(fixture.Shell, "WardrobeCape.owned"));
        WardrobePump(fixture, () => cape is not null); AssertEqual("owned", cape!.CapeId);
        WardrobePump(fixture, () => !WardrobeText(fixture, "WardrobeStatus").Contains("正在", StringComparison.Ordinal));
        AssertEqual(idleReads + 2, readCount);
        cape = null; Emit(fixture.Intents, "ui.wardrobe.cape.clear");
        WardrobePump(fixture, () => cape is not null); AssertTrue(cape!.CapeId is null);
        AssertFalse(fixture.Shell.Render(new(810, 2400)).Nodes.Any(n => n.Text?.Contains("PRIVATE", StringComparison.Ordinal) == true));
    }
    private static void WardrobePageRetiresLateQueriesPickersAndPreviews()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        foreach (string name in new[] { "Alice", "Bob" })
            AssertTrue(fixture.Service.AddProfile(new LaunchProfile { Username = name, Kind = LaunchProfileKind.Microsoft, Uuid = name }).IsSuccess);
        var first = new TaskCompletionSource<XsrResult<AccountWardrobeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountWardrobeSnapshot old = WardrobeDesktopSnapshot(fixture, "Old cape");
        int reads = 0, previews = 0; CancellationToken firstToken = default, pickerToken = default;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1) { firstToken = token; return new(first.Task); }
            return ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "Current cape")));
        });
        queries.Register<AccountWardrobeSkinQuery, AccountWardrobeSkinPreview>(AccountWardrobeContract.ValidateSkin, (_, _) =>
        { previews++; return ValueTask.FromResult(XsrResult.Success(new AccountWardrobeSkinPreview(WardrobeDesktopSkin(), false, "late.png"))); });
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        page.ConfigureFilePicker(token => { pickerToken = token; return picker.Task; });
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Render(new(810, 2400));
        AssertTrue(fixture.Service.SelectProfile(1) is null);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity").Contains("Bob", StringComparison.Ordinal));
        AssertTrue(firstToken.IsCancellationRequested);
        first.SetResult(XsrResult.Success(old)); fixture.Shell.Render(new(810, 2400));
        AssertFalse(fixture.Shell.Render(new(810, 2400)).Nodes.Any(n => n.Text?.Contains("Old cape", StringComparison.Ordinal) == true));
        Emit(fixture.Intents, "ui.wardrobe.browse"); fixture.Shell.Render(new(810, 2400));
        AssertTrue(fixture.Service.SelectProfile(0) is null);
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity").Contains("Alice", StringComparison.Ordinal));
        AssertTrue(pickerToken.IsCancellationRequested); picker.SetResult("/late/skin.png");
        fixture.Shell.Render(new(810, 2400)); AssertEqual(0, previews);
        AssertEqual("", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(FindEntity(fixture.Shell, "WardrobeSkinPath"))!.ReadDraft());
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(FindEntity(fixture.Shell, "WardrobeTexturePreview"))!.Raster is null);
        var retiredHead = FindEntity(fixture.Shell, "WardrobeHeadPreview");
        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Shell.Render(new(810, 2400));
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
        WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity").Contains("Alice", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.wardrobe.cape.clear");
        WardrobePump(fixture, () => reads >= 2 && fixture.Shell.Tree.GetComponent<XsrUiInput>(FindEntity(fixture.Shell, "WardrobeCapeClear"))!.Enabled);
        AssertEqual("Provider failure", WardrobeText(fixture, "WardrobeStatus"));
        Emit(fixture.Intents, "ui.wardrobe.cape.clear"); WardrobePump(fixture, () => attempts == 2);
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
        for (int frame = 0; frame < 20; frame++) fixture.Shell.Render(new(810, 2400));
        AssertEqual(1, failedReads);
    }
    private static void WardrobePagePaginatesCapeInventory()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Service.ConfigureRegionalPolicy(new("US"));
        fixture.Service.AddProfile(new LaunchProfile { Username = "Alice", Kind = LaunchProfileKind.Microsoft, Uuid = "alice" });
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
        using var page = new WardrobePageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            new XsrCommandRouterBuilder().Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(page.Page);
        var list = FindEntity(fixture.Shell, "WardrobeCapes");
        WardrobePump(fixture, () => fixture.Shell.Tree.Children(list).Count > 0);
        AssertEqual(WardrobePageController.CapePageSize, fixture.Shell.Tree.Children(list).Count);
        Emit(fixture.Intents, "ui.wardrobe.cape.next"); fixture.Shell.Render(new(810, 2400));
        AssertEqual("WardrobeCape.12", fixture.Shell.Tree.Name(fixture.Shell.Tree.Children(list)[0]));
        AssertEqual(WardrobePageController.CapePageSize, fixture.Shell.Tree.Children(list).Count);
        Emit(fixture.Intents, "ui.wardrobe.cape.next"); fixture.Shell.Render(new(810, 2400));
        AssertEqual(6, fixture.Shell.Tree.Children(list).Count);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(FindEntity(fixture.Shell, "WardrobeCapeNext"))!.Enabled);
    }
    private static PngImage WardrobeDesktopSkin()
    {
        using var resource = typeof(Nexa.UI.Next.Backend.Avalonia.AvaloniaUiShellHost).Assembly
            .GetManifestResourceStream("Nexa.UI.Next.Backend.Avalonia.Assets.Avatars.Steve.png")!;
        using var buffer = new MemoryStream(); resource.CopyTo(buffer); return PngImage.TryCreate(buffer.ToArray())!;
    }
    private static string WardrobeText(LaunchPageFixture fixture, string key)
        => fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, key))!.Content;
    private static void WardrobePump(LaunchPageFixture fixture, Func<bool> done)
    {
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(810, 2400)); return done(); }, TimeSpan.FromSeconds(5)));
    }
}
