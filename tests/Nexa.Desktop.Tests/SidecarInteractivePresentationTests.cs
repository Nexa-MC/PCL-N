using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.Sidecar.Protocol;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ResourceInteractiveModulePresentsLiveStateAndCancelsRetiredSource()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (_, _) =>
            ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([], 0, 0))));
        DesktopSidecarUiPatches ui = new(fixture.Store);
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { }, sidecarUi: ui);
        fixture.Shell.Stage.Navigation.Replace(page.Page); fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Renderer.TextLocalizer = source => source is "输入" or "状态" ? "Translated host caption" : source;
        fixture.Shell.Render(new(1000, 650)); fixture.Shell.Render(new(1000, 650));
        byte[] payload = new SidecarUiDocument(DesktopSidecarUiPatches.ResourceCard.Value, "交互扩展", "插件表单", [
            new(1, 0, SidecarUiNodeKind.Text, "状态", ValueState: "plugin.state.text"),
            new(2, 0, SidecarUiNodeKind.TextInput, "输入", "plugin.command", ValueState: "plugin.state.text", VisibleState: "plugin.state.enabled"),
            new(3, 0, SidecarUiNodeKind.Toggle, "启用", "plugin.toggle", ValueState: "plugin.state.enabled")]).Encode();
        var declarations = new[] {
            new SidecarRegistrationItem(SidecarRegistrationKind.Command, "plugin.command", 0, 0),
            new(SidecarRegistrationKind.Command, "plugin.toggle", 0, 1),
            new(SidecarRegistrationKind.State, "plugin.state.text", 0, 0),
            new(SidecarRegistrationKind.State, "plugin.state.enabled", 0, 1),
            new(SidecarRegistrationKind.UiModule, "plugin.interactive", 0, 0, payload, SHA256.HashData(payload)) };
        var (session, peer) = ActivateCaptionPatch(new(), "", declarations: declarations, uiPatches: ui,
            features: SidecarFeatures.BinaryPayloads, initialState: [(1, Encoding.UTF8.GetBytes("初始")), (2, new byte[] { 1 })]).GetAwaiter().GetResult();
        using (session) using (peer)
        {
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == "状态 初始"));
            AssertTrue(scene.Nodes.Any(node => node.Text == "输入"));
            AssertFalse(scene.Nodes.Any(node => node.Text == "Translated host caption"));
            // The host owns a bounded 96px scrolling slot. The introductory text is
            // visible first; use its actual wheel route to expose the form below it.
            AssertFalse(HasKey(fixture.Shell, scene, "ResourcePluginNode.2"));
            AssertTrue(FindByKey(fixture.Shell, scene, "ResourceExtensionCard").Scroll!.Value.MaximumOffsetY > 0);
            // Input helpers are addressed by their rendered stable key, while the
            // controller's named action table contains buttons only.
            var input = ShowModuleControl("ResourcePluginNode.2").Entity;
            var submit = page.Find("ResourcePluginNode.2.Submit"); var toggle = page.Find("ResourcePluginNode.3");
            AssertEqual("初始", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
            fixture.Shell.Renderer.SetTextInputValue(input, "用户草稿");
            for (int i = 0; i < 8; i++) scene = fixture.Shell.Render(new(1000, 650));
            AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourcePluginNode.2").Entity);
            AssertEqual("用户草稿", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
            var loop = session.RunReceiveLoopAsync().AsTask();
            AssertEqual(submit, ShowModuleControl("ResourcePluginNode.2.Submit").Entity);
            Emit(fixture.Intents, "ui.resources.action", submit); fixture.Shell.Render(new(1000, 650));
            var request = peer.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var decoded = SidecarDataMessages.DecodeBinaryRequest(request.Payload.Span); AssertEqual(1u, decoded.ContractId);
            AssertEqual("用户草稿", Encoding.UTF8.GetString(decoded.Value.Span));
            peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.CommandResult, SidecarFrameTraits.Final, request.CorrelationId,
                SidecarDataMessages.EncodeResult(true, "", null))).AsTask().GetAwaiter().GetResult();
            // Mirror changes update the existing controls; plugin labels remain literal even
            // when their spelling matches a host catalog caption. Hidden input labels retire too.
            ChangeState(1, Encoding.UTF8.GetBytes("远端更新"));
            AssertEqual(input, FindByKey(fixture.Shell, scene, "ResourcePluginNode.2").Entity);
            AssertEqual("远端更新", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
            fixture.Shell.Renderer.SetTextInputValue(input, "用户草稿");
            ChangeState(2, [0]);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiElement>(input)!.IsVisible);
            AssertFalse(fixture.Shell.Render(new(1000, 650)).Nodes.Any(node => node.Text == "输入"));
            ChangeState(2, [1]);
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiElement>(input)!.IsVisible);
            AssertEqual(input, ShowModuleControl("ResourcePluginNode.2").Entity);
            AssertEqual("用户草稿", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
            AssertEqual(toggle, ShowModuleControl("ResourcePluginNode.3").Entity);
            Emit(fixture.Intents, "ui.resources.action", toggle); fixture.Shell.Render(new(1000, 650));
            request = peer.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            decoded = SidecarDataMessages.DecodeBinaryRequest(request.Payload.Span); AssertEqual(2u, decoded.ContractId);
            AssertEqual(SidecarWireCodecs.Bool, decoded.Value.CodecId); AssertEqual((byte)0, decoded.Value.Span[0]);
            peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.CommandResult, SidecarFrameTraits.Final, request.CorrelationId,
                SidecarDataMessages.EncodeResult(true, "", null))).AsTask().GetAwaiter().GetResult();
            // A pending form action is canceled when its source page is left. It cannot execute
            // again from a captured entity after a fresh page exposure.
            AssertEqual(submit, ShowModuleControl("ResourcePluginNode.2.Submit").Entity);
            Emit(fixture.Intents, "ui.resources.action", submit); fixture.Shell.Render(new(1000, 650));
            request = peer.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            fixture.Shell.Stage.Navigation.Replace(page.DetailPage); fixture.Shell.Render(new(1000, 650));
            var cancel = peer.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            AssertEqual(SidecarMessageType.Cancel, cancel.MessageType); AssertEqual(request.CorrelationId, cancel.CorrelationId);
            fixture.Shell.Stage.Navigation.Replace(page.Page); scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(fixture.Shell.Tree.IsAlive(input));
            AssertTrue(input != ShowModuleControl("ResourcePluginNode.2").Entity);
            session.Dispose(); loop.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            scene = fixture.Shell.Render(new(1000, 650));
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiElement>(page.Find("ResourceExtensionCard"))!.IsVisible);
            AssertFalse(scene.Nodes.Any(node => node.Text == "交互扩展"));

            void ChangeState(uint id, byte[] value)
            {
                long revision = fixture.Store.Read<XsrUiModuleSnapshot>(ui.ModuleState).Revision;
                peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateDelta, SidecarFrameTraits.None, SidecarCorrelationId.Create(),
                    SidecarDataMessages.EncodeStateDelta(id, value))).AsTask().GetAwaiter().GetResult();
                AssertTrue(SpinWait.SpinUntil(() => fixture.Store.Read<XsrUiModuleSnapshot>(ui.ModuleState).Revision > revision, TimeSpan.FromSeconds(3)));
                scene = fixture.Shell.Render(new(1000, 650));
            }

            XsrUiSceneNode ShowModuleControl(string key)
            {
                for (int step = 0; step < 16; step++)
                {
                    var viewport = FindByKey(fixture.Shell, scene, "ResourceExtensionCard");
                    var control = scene.Nodes.FirstOrDefault(node => fixture.Shell.Tree.Name(node.Entity) == key);
                    if (control.Entity.IsAssigned && control.Rect.Y >= viewport.Rect.Y
                        && control.Rect.Y + control.Rect.Height <= viewport.Rect.Y + viewport.Rect.Height + .01)
                    {
                        AssertTrue(control.IsAccessible && control.IsEnabled);
                        AssertEqual(control.Entity, fixture.Shell.Renderer.HitTest(new(control.Rect.X + control.Rect.Width / 2,
                            control.Rect.Y + control.Rect.Height / 2)));
                        return control;
                    }
                    double delta = Math.Max(1, viewport.Scroll!.Value.ViewportHeight / 2);
                    if (control.Entity.IsAssigned && control.Rect.Y < viewport.Rect.Y) delta = -delta;
                    AssertTrue(fixture.Shell.Renderer.PointerScroll(new(viewport.Rect.X + viewport.Rect.Width / 2,
                        viewport.Rect.Y + viewport.Rect.Height / 2), delta));
                    scene = fixture.Shell.Render(new(1000, 650));
                }
                throw new InvalidOperationException("Plugin control is not visible in its scrolling host slot: " + key);
            }
        }
    }
    private static void ResourcePrimitiveFunctionPatchChangesPresentationOnly()
    {
        DesktopFunctionPatches patches = new();
        AssertEqual(23L, ResourceCaptions.DownloadCount(patches.Runtime, patches.DownloadCount, 23L));
        byte[] payload = new byte[11 + 15]; "NFP2"u8.CopyTo(payload); payload[4] = 1; payload[5] = 4;
        payload[6] = 1; payload[7] = 3; payload[8] = 3; BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), 5);
        payload[11] = 2; payload[12] = 3; payload[13] = 3; BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(14), 8);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), 100L); payload[24] = 4; payload[25] = 6;
        // The program includes one terminal instruction after the result store.
        payload = payload.Concat(new byte[] { 8 }).ToArray();
        var (session, peer) = ActivateCaptionPatch(patches, "", declarations:
            [new(SidecarRegistrationKind.FunctionPatch, "plugin.download.count", 0, 0, payload, SHA256.HashData(payload),
                TargetSemanticId: DesktopFunctionPatches.DownloadCountTarget.Value)]).GetAwaiter().GetResult();
        using (session) using (peer)
        {
            AssertEqual(123L, ResourceCaptions.DownloadCount(patches.Runtime, patches.DownloadCount, 23L));
            session.Dispose(); AssertEqual(23L, ResourceCaptions.DownloadCount(patches.Runtime, patches.DownloadCount, 23L));
        }
    }
}
