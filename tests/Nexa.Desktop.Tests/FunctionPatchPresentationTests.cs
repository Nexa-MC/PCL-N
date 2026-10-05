using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourceFunctionPatchRewritesActualListAndDetailCaptions()
    {
        DesktopFunctionPatches patches = new();
        AssertEqual("original", ResourceCaptions.ProjectTitle(patches.Runtime, patches.ProjectTitle, "original"));
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        ResourceProject project = new("unchanged-id", "Original title", "Original description", "Author", 1, "https://modrinth.com/project/unchanged-id");
        ResourceDetailQuery? received = null;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([project], 1, 0))));
        queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail, (query, _) =>
        { received = query; return ValueTask.FromResult(XsrResult.Success(new ResourceDetail(project, "MIT", []))); });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Store, _ => { }, patches);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        _ = fixture.Shell.Render(new(1000, 650));
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(scene.Nodes.Any(node => node.Text == project.Title));
        var (session, plugin) = ActivateCaptionPatch(patches, "[扩展] ").GetAwaiter().GetResult();
        using (session) using (plugin)
        {
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
            _ = fixture.Shell.Render(new(1000, 650)); scene = fixture.Shell.Render(new(1000, 650));
            var caption = scene.Nodes.Single(node => node.Text == "[扩展] " + project.Title);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(caption.Entity)!.Localize);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiSemantic>(caption.Entity)!.Localize);
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceDetails.unchanged-id"));
            _ = fixture.Shell.Render(new(1000, 650)); scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(page.DetailPage, fixture.Shell.Stage.Navigation.Current);
            AssertTrue(scene.Nodes.Any(node => node.Text == "[扩展] " + project.Title));
            AssertTrue(received is not null);
            AssertEqual("unchanged-id", received!.ProjectId);
            AssertEqual("Original title", project.Title);
            session.Dispose();
            AssertEqual("Original title", ResourceCaptions.ProjectTitle(patches.Runtime, patches.ProjectTitle, project.Title));
            fixture.Shell.Stage.Navigation.Pop();
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
            _ = fixture.Shell.Render(new(1000, 650)); scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == project.Title));
            AssertFalse(scene.Nodes.Any(node => node.Text == "[扩展] " + project.Title));
        }
    }

    private static async Task<(SidecarHostSession Session, SidecarConnection Plugin)> ActivateCaptionPatch(DesktopFunctionPatches patches, string prefix,
        DesktopSidecarSignals? signals = null, SidecarRegistrationItem[]? declarations = null,
        DesktopSidecarUiPatches? uiPatches = null)
    {
        byte[] text = Encoding.UTF8.GetBytes(prefix);
        byte[] payload = new byte[15 + text.Length]; "NFP1"u8.CopyTo(payload); payload[4] = 1; payload[5] = 4; payload[6] = 5;
        payload[8] = 3; BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), checked((ushort)text.Length)); text.CopyTo(payload, 11);
        new byte[] { 2, 4, 6, 8 }.CopyTo(payload, 11 + text.Length);
        var (host, peer) = SidecarLoopbackStream.CreatePair();
        SidecarHostSession session = new(new SidecarConnection(host), "CaptionFixture")
        { FunctionPatchAdmission = patches.Admission, SignalAdmission = signals?.Admission, UiPatchAdmission = uiPatches?.Admission, UiModuleAdmission = uiPatches?.ModuleAdmission };
        SidecarConnection plugin = new(peer);
        try
        {
            ValueTask Send(SidecarMessageType type, byte[] bytes) => plugin.SendAsync(new(SidecarProtocol.Version, type,
                SidecarFrameTraits.None, SidecarCorrelationId.Create(), bytes));
            var handshake = session.HandshakeAsync(); var hello = await plugin.ReceiveAsync();
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome,
                SidecarFrameTraits.None, hello.CorrelationId,
                SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid())));
            await handshake;
            var registration = session.AcceptRegistrationAsync();
            declarations ??= [new(SidecarRegistrationKind.FunctionPatch,
                "plugin.caption", 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: DesktopFunctionPatches.ProjectTitleTarget.Value)];
            await Send(SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin((uint)declarations.Length));
            foreach (var declaration in declarations)
                await Send(SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(declaration));
            await Send(SidecarMessageType.RegisterEnd, SidecarRegistration.EncodeEnd()); await registration;
            var snapshot = session.AcceptStateSnapshotAsync();
            await Send(SidecarMessageType.StateSnapshotBegin, SidecarStateSnapshot.EncodeBegin(0));
            await Send(SidecarMessageType.StateSnapshotEnd, []); await snapshot; _ = await plugin.ReceiveAsync();
            await session.ActivateAsync(); _ = await plugin.ReceiveAsync();
            return (session, plugin);
        }
        catch { session.Dispose(); plugin.Dispose(); throw; }
    }

    private static void ResourceSignalAdapterCatchesOnlySearchAndRetiresWithSession()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        int searches = 0;
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (_, _) =>
        { searches++; return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([], 0, 0))); });
        DesktopSidecarSignals signals = new();
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Store, _ => { }, sidecarSignals: signals);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Render(new(1000, 650)); fixture.Shell.Render(new(1000, 650));
        AssertEqual(1, searches);
        byte[] catchPayload = [.. "NXS1"u8.ToArray(), 1];
        byte[] listenPayload = [.. "NXS1"u8.ToArray(), 0];
        var (session, peer) = ActivateCaptionPatch(new(), "", signals,
            [new(SidecarRegistrationKind.IntentCatch, "plugin.search", 0, 0, catchPayload, SHA256.HashData(catchPayload), TargetSemanticId: "ui.resources.search.v1"),
             new(SidecarRegistrationKind.EventListen, "plugin.completed", 0, 0, listenPayload, SHA256.HashData(listenPayload), TargetSemanticId: "event.resources.search-completed.v1")]).GetAwaiter().GetResult();
        using (session) using (peer)
        {
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
            fixture.Shell.Render(new(1000, 650));
            AssertEqual(1, searches);
            var caught = Read(); AssertEqual(SidecarRegistrationKind.IntentCatch, caught.Kind); AssertEqual("search", caught.Value);
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceReset"));
            fixture.Shell.Render(new(1000, 650)); fixture.Shell.Render(new(1000, 650));
            AssertEqual(2, searches);
            var completed = Read(); AssertEqual(SidecarRegistrationKind.EventListen, completed.Kind); AssertEqual("success", completed.Value);
            session.Dispose();
            Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceSearchButton"));
            fixture.Shell.Render(new(1000, 650));
            AssertEqual(3, searches);
        }
        SidecarHookSignal Read()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = peer.ReceiveAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            AssertEqual(SidecarMessageType.HookSignal, frame.MessageType);
            return SidecarHookSignal.Decode(frame.Payload.Span);
        }
    }

    private static void ResourceModuleRendersLiteralTextWithoutActionsAndRetiresWithoutSpace()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (_, _) =>
            ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([], 0, 0))));
        DesktopSidecarUiPatches ui = new(fixture.Store);
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Store, _ => { }, sidecarUi: ui);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Render(new(1000, 650)); fixture.Shell.Render(new(1000, 650));
        var slot = page.Find("ResourceExtensionCard");
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiElement>(slot)!.IsVisible);
        var search = page.Find("ResourceSearchButton");
        byte[] payload = new SidecarUiCard(DesktopSidecarUiPatches.ResourceCard.Value, "扩展 {title}", new string('测', 400)).Encode();
        var (session, peer) = ActivateCaptionPatch(new(), "", declarations:
            [new(SidecarRegistrationKind.UiModule, "plugin.card", 0, 0, payload, SHA256.HashData(payload))], uiPatches: ui).GetAwaiter().GetResult();
        using (session) using (peer)
        {
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiElement>(slot)!.IsVisible);
            var title = scene.Nodes.Single(node => node.Text == "扩展 {title}");
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(title.Entity)!.Localize);
            AssertEqual("扩展 {title}", fixture.Shell.Tree.GetComponent<XsrUiSemantic>(title.Entity)!.Label);
            foreach (var child in fixture.Shell.Tree.Children(slot))
                AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiCommandBinding>(child) is null);
            AssertEqual(96d, fixture.Shell.Tree.GetComponent<XsrUiElement>(slot)!.Height!.Value);
            AssertTrue(scene.Nodes.Single(node => node.Entity == slot).Scroll!.Value.ContentHeight > 96);
            AssertEqual(search, page.Find("ResourceSearchButton"));
            session.Dispose();
            scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiElement>(slot)!.IsVisible);
            AssertEqual(0, fixture.Shell.Tree.Children(slot).Count);
            AssertFalse(scene.Nodes.Any(node => node.Text == "扩展 {title}"));
            AssertEqual(page.Page, fixture.Shell.Stage.Navigation.Current);
        }
    }

    private static void ResourceCaptionPatchReadsStateAndRestoresTextAndAccessibility()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        int searches = 0;
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (_, _) =>
        { searches++; return ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([], 0, 0))); });
        DesktopSidecarUiPatches ui = new(fixture.Store);
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Store, _ => { }, sidecarUi: ui);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Render(new(1000, 650)); fixture.Shell.Render(new(1000, 650));
        var button = page.Find("ResourceSearchButton");
        byte[] payload = SidecarUiCaptionPatch.Encode("检索");
        var (session, peer) = ActivateCaptionPatch(new(), "", declarations:
            [new(SidecarRegistrationKind.UiPatch, "plugin.search-caption", 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: DesktopSidecarUiPatches.SearchLabel.Value)],
            uiPatches: ui).GetAwaiter().GetResult();
        using (session) using (peer)
        {
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Entity == button && node.Text == "检索"));
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(button)!.Localize);
            AssertEqual("检索", fixture.Shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label);
            Emit(fixture.Intents, "ui.resources.action", button); fixture.Shell.Render(new(1000, 650));
            AssertEqual(2, searches); // A caption patch cannot replace the command.
            session.Dispose();
            scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Entity == button && node.Text == "搜索"));
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiText>(button)!.Localize);
            AssertEqual("搜索", fixture.Shell.Tree.GetComponent<XsrUiSemantic>(button)!.Label);
            AssertEqual(button, page.Find("ResourceSearchButton"));
        }
    }
}
