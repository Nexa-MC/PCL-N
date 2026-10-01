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

    private static async Task<(SidecarHostSession Session, SidecarConnection Plugin)> ActivateCaptionPatch(DesktopFunctionPatches patches, string prefix)
    {
        byte[] text = Encoding.UTF8.GetBytes(prefix);
        byte[] payload = new byte[15 + text.Length]; "NFP1"u8.CopyTo(payload); payload[4] = 1; payload[5] = 4; payload[6] = 5;
        payload[8] = 3; BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), checked((ushort)text.Length)); text.CopyTo(payload, 11);
        new byte[] { 2, 4, 6, 8 }.CopyTo(payload, 11 + text.Length);
        var (host, peer) = SidecarLoopbackStream.CreatePair();
        SidecarHostSession session = new(new SidecarConnection(host), "CaptionFixture") { FunctionPatchAdmission = patches.Admission };
        SidecarConnection plugin = new(peer);
        try
        {
            ValueTask Send(SidecarMessageType type, byte[] bytes) => plugin.SendAsync(new(SidecarProtocol.Version, type,
                SidecarFrameTraits.None, SidecarCorrelationId.Create(), bytes));
            var handshake = session.HandshakeAsync(); var hello = await plugin.ReceiveAsync();
            await Send(SidecarMessageType.Welcome, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid()));
            await handshake;
            var registration = session.AcceptRegistrationAsync();
            await Send(SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin(1));
            await Send(SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(new(SidecarRegistrationKind.FunctionPatch,
                "plugin.caption", 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: DesktopFunctionPatches.ProjectTitleTarget.Value)));
            await Send(SidecarMessageType.RegisterEnd, SidecarRegistration.EncodeEnd()); await registration;
            var snapshot = session.AcceptStateSnapshotAsync();
            await Send(SidecarMessageType.StateSnapshotBegin, SidecarStateSnapshot.EncodeBegin(0));
            await Send(SidecarMessageType.StateSnapshotEnd, []); await snapshot; _ = await plugin.ReceiveAsync();
            await session.ActivateAsync(); _ = await plugin.ReceiveAsync();
            return (session, plugin);
        }
        catch { session.Dispose(); plugin.Dispose(); throw; }
    }
}
