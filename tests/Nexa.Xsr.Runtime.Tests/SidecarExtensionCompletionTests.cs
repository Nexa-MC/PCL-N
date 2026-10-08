using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId ScalarPoint = XsrSemanticId.Parse("ui.fixture.scalar.v2");
    private static byte[] ScalarProgram(XsrFunctionShape shape, XsrFunctionPatchPhase phase, ushort count, params byte[] operations)
    {
        byte[] payload = new byte[10 + shape.Arguments.Count + operations.Length]; "NFP2"u8.CopyTo(payload);
        payload[4] = 1; payload[5] = (byte)phase; payload[6] = (byte)shape.Arguments.Count; payload[7] = (byte)shape.Result;
        for (int i = 0; i < shape.Arguments.Count; i++) payload[8 + i] = (byte)shape.Arguments[i];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8 + shape.Arguments.Count), count);
        operations.CopyTo(payload, 10 + shape.Arguments.Count); return payload;
    }
    private static byte[] ScalarConstant(uint codec, object value)
    {
        byte[] encoded = SidecarWireCodecs.Encode(codec, value), result = new byte[4 + encoded.Length];
        result[0] = 3; result[1] = (byte)codec; BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), checked((ushort)encoded.Length));
        encoded.CopyTo(result, 4); return result;
    }
    private static SidecarRegistrationItem ScalarItem(string semantic, byte[] payload) => new(SidecarRegistrationKind.FunctionPatch,
        semantic, 0, 0, payload, SHA256.HashData(payload), TargetSemanticId: ScalarPoint.Value);

    private static async ValueTask PrimitiveFunctionAbiExecutesTypedPhasesAndRollsBackOverflow()
    {
        var shape = new XsrFunctionShape(XsrFunctionValueKind.Int64, XsrFunctionValueKind.Int64, XsrFunctionValueKind.Int64);
        XsrFunctionPatchRuntime runtime = new(new XsrFunctionTarget(ScalarPoint, shape));
        var point = runtime.Resolve(ScalarPoint);
        XsrFunctionOriginal original = static values => XsrFunctionValue.From(values[0].AsInt64() + values[1].AsInt64());
        var items = new[]
        {
            ScalarItem("patch.scalar.args", ScalarProgram(shape, XsrFunctionPatchPhase.Args, 5, [1, 0, .. ScalarConstant(3, 10L), 4, 5, 0, 8])),
            ScalarItem("patch.scalar.return", ScalarProgram(shape, XsrFunctionPatchPhase.Return, 5, [2, .. ScalarConstant(3, 2L), 4, 6, 8]))
        };
        var (session, peer) = await PatchSession(runtime, items, admission: new(runtime, ScalarPoint));
        using (session) using (peer)
        {
            AssertEqual(3L, runtime.InvokeValues(point, [XsrFunctionValue.From(1L), XsrFunctionValue.From(2L)], original).AsInt64());
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertEqual(15L, runtime.InvokeValues(point, [XsrFunctionValue.From(1L), XsrFunctionValue.From(2L)], original).AsInt64());
            AssertThrows<ArgumentException>(() => runtime.InvokeValues(point, [XsrFunctionValue.From(1), XsrFunctionValue.From(2)], original));
            AssertThrows<ArgumentException>(() => runtime.Invoke(point, "bad shape", static value => value));
            await session.DeactivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertEqual(3L, runtime.InvokeValues(point, [XsrFunctionValue.From(1L), XsrFunctionValue.From(2L)], original).AsInt64());
        }
        var (faulted, faultPeer) = await PatchSession(runtime,
            [ScalarItem("patch.scalar.fault", ScalarProgram(shape, XsrFunctionPatchPhase.Args, 7,
                [.. ScalarConstant(3, 99L), 5, 1, 1, 0, .. ScalarConstant(3, 1L), 4, 5, 0, 8]))], admission: new(runtime, ScalarPoint));
        using (faulted) using (faultPeer)
        {
            await SnapshotAll(faulted, faultPeer); await faulted.ActivateAsync(); await DataPlaneReceiveAsync(faultPeer);
            AssertEqual(long.MaxValue, runtime.InvokeValues(point, [XsrFunctionValue.From(long.MaxValue), XsrFunctionValue.From(0L)], original).AsInt64());
            AssertEqual(3L, runtime.InvokeValues(point, [XsrFunctionValue.From(1L), XsrFunctionValue.From(2L)], original).AsInt64());
        }
    }
    private static async ValueTask PrimitiveFunctionAbiRejectsInvalidShapesAndRetiresBeforeReturn()
    {
        var shape = new XsrFunctionShape(XsrFunctionValueKind.Boolean, XsrFunctionValueKind.Int32, XsrFunctionValueKind.Int32);
        XsrFunctionPatchRuntime runtime = new(new XsrFunctionTarget(ScalarPoint, shape)); var point = runtime.Resolve(ScalarPoint);
        byte[] valid = ScalarProgram(shape, XsrFunctionPatchPhase.Replace, 6, [1, 0, 1, 1, 9, 6, 7, 8]);
        var (session, peer) = await PatchSession(runtime, [ScalarItem("patch.equal", valid)], admission: new(runtime, ScalarPoint));
        using (session) using (peer)
        {
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertTrue(runtime.InvokeValues(point, [XsrFunctionValue.From(2), XsrFunctionValue.From(2)], _ => throw new InvalidOperationException()).AsBoolean());
            AssertFalse(runtime.InvokeValues(point, [XsrFunctionValue.From(2), XsrFunctionValue.From(3)], _ => throw new InvalidOperationException()).AsBoolean());
            session.Dispose(); AssertFalse(runtime.HasPatches(point));
        }
        foreach (byte[] malformed in new[]
        {
            valid[..^1], valid.Concat(new byte[] { 0 }).ToArray(),
            ScalarProgram(shape, XsrFunctionPatchPhase.Return, 2, [1, 3, 8]),
            ScalarProgram(shape, XsrFunctionPatchPhase.Return, 3, [.. ScalarConstant(3, 1L), 6, 8]),
            ScalarProgram(new(XsrFunctionValueKind.Int32, XsrFunctionValueKind.Int32), XsrFunctionPatchPhase.Head, 1, [8]),
            ScalarProgram(shape, XsrFunctionPatchPhase.Args, 2, [2, 8])
        })
            await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(runtime,
                [ScalarItem("patch.invalid", malformed)], admission: new(runtime, ScalarPoint)).AsTask());
        AssertThrows<ArgumentException>(() => XsrFunctionValue.From(double.NaN));
        AssertThrows<ArgumentException>(() => _ = new XsrFunctionShape(XsrFunctionValueKind.Boolean, XsrFunctionValueKind.Void));

        var tail = ScalarProgram(shape, XsrFunctionPatchPhase.Return, 3, [.. ScalarConstant(1, true), 6, 8]);
        var (retiring, retiringPeer) = await PatchSession(runtime, [ScalarItem("patch.tail", tail)], admission: new(runtime, ScalarPoint));
        using (retiring) using (retiringPeer)
        {
            await SnapshotAll(retiring, retiringPeer); await retiring.ActivateAsync(); await DataPlaneReceiveAsync(retiringPeer);
            AssertFalse(runtime.InvokeValues(point, [XsrFunctionValue.From(1), XsrFunctionValue.From(2)], _ => { retiring.Dispose(); return XsrFunctionValue.From(false); }).AsBoolean());
        }
    }

    private static SidecarRegistrationItem InteractiveItem(string id, SidecarUiNode[] nodes, string? resources = null)
    {
        byte[] payload = new SidecarUiDocument(ModuleSlot.Value, "Interactive title", "Host-owned plugin slot", nodes).Encode();
        return new(SidecarRegistrationKind.UiModule, id, 0, 0, payload, SHA256.HashData(payload), resources);
    }
    private static async ValueTask InteractiveDocumentsBindNumericCommandsAndLiveState()
    {
        var (store, runtime, state) = ModuleRuntime();
        var nodes = new[]
        {
            new SidecarUiNode(1, 0, SidecarUiNodeKind.Stack, ""),
            new(2, 1, SidecarUiNodeKind.Text, "Status", ValueState: "plugin.state.text"),
            new(3, 1, SidecarUiNodeKind.Button, "Execute", "plugin.command", EnabledState: "plugin.state.enabled", Argument: "bounded")
        };
        var items = new[]
        {
            new SidecarRegistrationItem(SidecarRegistrationKind.Command, "plugin.command", 0, 0),
            new(SidecarRegistrationKind.State, "plugin.state.text", 0, 0),
            new(SidecarRegistrationKind.State, "plugin.state.enabled", 0, 1), InteractiveItem("plugin.document", nodes)
        };
        var (session, peer) = await PatchSession(new(CaptionPoint), items, uiModules: new(runtime, ModuleSlot));
        using (session) using (peer)
        {
            await SnapshotAll(session, peer, (1, Encoding.UTF8.GetBytes("initial")), (2, new byte[] { 1 }));
            await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            var original = store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0)!;
            AssertEqual("initial", original.Nodes[1].Value); AssertTrue(original.Nodes[2].Enabled);
            var loop = session.RunReceiveLoopAsync().AsTask();
            var sending = runtime.DispatchAsync(0, original.Activation, 3).AsTask();
            var request = await DataPlaneReceiveAsync(peer); AssertEqual(SidecarMessageType.CommandRequest, request.MessageType);
            var decoded = SidecarDataMessages.DecodeRequest(request.Payload.Span); AssertEqual(1u, decoded.ContractId); AssertEqual("bounded", decoded.Argument);
            await peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.CommandResult, SidecarFrameTraits.Final, request.CorrelationId,
                SidecarDataMessages.EncodeResult(true, "", null)));
            AssertTrue((await sending.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);
            long revision = store.Read<XsrUiModuleSnapshot>(state).Revision;
            await peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateDelta, SidecarFrameTraits.None, SidecarCorrelationId.Create(),
                SidecarDataMessages.EncodeStateDelta(1, Encoding.UTF8.GetBytes("updated"))));
            await WaitModuleRevision(store, state, revision);
            var updated = store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0)!;
            AssertEqual("updated", updated.Nodes[1].Value); AssertEqual(original.Activation, updated.Activation); AssertEqual("initial", original.Nodes[1].Value);
            revision = store.Read<XsrUiModuleSnapshot>(state).Revision;
            await peer.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.StateDelta, SidecarFrameTraits.None, SidecarCorrelationId.Create(),
                SidecarDataMessages.EncodeStateDelta(2, new byte[] { 0 })));
            await WaitModuleRevision(store, state, revision);
            AssertFalse((await runtime.DispatchAsync(0, original.Activation, 3)).IsSuccess);
            session.Dispose(); await loop.WaitAsync(TimeSpan.FromSeconds(3));
            AssertTrue(store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0) is null);
            AssertFalse((await runtime.DispatchAsync(0, original.Activation, 3)).IsSuccess);
        }
    }
    private static async Task WaitModuleRevision(XsrStateStore store, XsrStateId state, long previous)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (store.Read<XsrUiModuleSnapshot>(state).Revision <= previous) await Task.Delay(1, stop.Token);
    }
    private static async ValueTask InteractiveDocumentsRejectCrossSessionBindingsAndBadImages()
    {
        var (store, runtime, state) = ModuleRuntime();
        var command = new SidecarRegistrationItem(SidecarRegistrationKind.Command, "plugin.command", 0, 0);
        var text = new SidecarRegistrationItem(SidecarRegistrationKind.State, "plugin.text", 0, 0);
        foreach (var nodes in new SidecarUiNode[][]
        {
            [new(1, 0, SidecarUiNodeKind.Button, "Bad", "host.delete")],
            [new(1, 0, SidecarUiNodeKind.Text, "Bad", ValueState: "host.accounts")],
            [new(1, 0, SidecarUiNodeKind.Button, "Bad", "plugin.command", EnabledState: "plugin.text")],
            [new(1, 0, SidecarUiNodeKind.Image, "Bad", Resource: "plugin.image")],
            [new(1, 0, SidecarUiNodeKind.Toggle, "Bad", "plugin.command", ValueState: "plugin.text")]
        })
            await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(new(CaptionPoint),
                [command, text, InteractiveItem("plugin.bad", nodes)], uiModules: new(runtime, ModuleSlot)).AsTask());
        AssertTrue(store.Read<XsrUiModuleSnapshot>(state).Value is null);
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarUiDocument(ModuleSlot.Value, "Title", "Body", [new(1, 1, SidecarUiNodeKind.Text, "cycle")]));
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarUiDocument(ModuleSlot.Value, "Title", "Body",
            [new(1, 0, SidecarUiNodeKind.Text, "not a container"), new(2, 1, SidecarUiNodeKind.Text, "child")]));
        var list = new List<SidecarUiNode> { new(1, 0, SidecarUiNodeKind.Text, "Original") };
        var document = new SidecarUiDocument(ModuleSlot.Value, "Title", "Body", list); list.Clear();
        AssertEqual("Original", SidecarUiDocument.Decode(document.Encode()).Nodes[0].Label);
        AssertThrows<NotSupportedException>(() => ((IList<SidecarUiNode>)document.Nodes).Clear());
        var forged = InteractiveItem("plugin.image.module", [new(1, 0, SidecarUiNodeKind.Image, "Bad", Resource: "plugin.image")], "plugin.image");
        byte[] invalid = [1, 2, 3]; var resource = new SidecarRegistrationItem(SidecarRegistrationKind.Resource, "plugin.image", 0, 0, invalid, SHA256.HashData(invalid));
        await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(new(CaptionPoint), [resource, forged], uiModules: new(runtime, ModuleSlot)).AsTask());
    }
    private static async ValueTask InteractiveImagesUseOwnedVerifiedResourcesAndPixelBudgets()
    {
        var (store, runtime, state) = ModuleRuntime();
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
        var resource = new SidecarRegistrationItem(SidecarRegistrationKind.Resource, "plugin.image", 0, 0, png, SHA256.HashData(png));
        var item = InteractiveItem("plugin.image.module", [new(1, 0, SidecarUiNodeKind.Image, "Verified image", Resource: "plugin.image")], "plugin.image");
        var (session, peer) = await PatchSession(new(CaptionPoint), [resource, item], uiModules: new(runtime, ModuleSlot));
        using (session) using (peer)
        {
            png[0] = 0; // Both cache and admitted image own their bytes.
            await SnapshotAll(session, peer); await session.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            var view = store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0)!;
            AssertEqual(1, view.Nodes[0].Image!.Width); AssertEqual((byte)137, view.Nodes[0].Image!.Bytes.Span[0]);
            AssertFalse((await runtime.DispatchAsync(0, view.Activation, 1)).IsSuccess);
        }
        // A forged container header cannot make the projection reserve unbounded native pixels.
        byte[] oversized = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
        BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(16), 1024); BinaryPrimitives.WriteInt32BigEndian(oversized.AsSpan(20), 1024);
        resource = resource with { Payload = oversized, ContentHash = SHA256.HashData(oversized) };
        item = InteractiveItem("plugin.budget.module", Enumerable.Range(1, 3).Select(id =>
            new SidecarUiNode((ushort)id, 0, SidecarUiNodeKind.Image, "Large", Resource: "plugin.image")).ToArray(), "plugin.image");
        await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(new(CaptionPoint), [resource, item], uiModules: new(runtime, ModuleSlot)).AsTask());
    }

    private static async ValueTask InteractiveDocumentReplacementCancelsPendingAndRevokesOldIdentity()
    {
        var (store, runtime, state) = ModuleRuntime();
        var items = new[] { new SidecarRegistrationItem(SidecarRegistrationKind.Command, "plugin.command", 0, 0),
            InteractiveItem("plugin.document", [new(1, 0, SidecarUiNodeKind.Button, "Execute", "plugin.command")]) };
        var (first, firstPeer) = await PatchSession(new(CaptionPoint), items, uiModules: new(runtime, ModuleSlot));
        var (second, secondPeer) = await PatchSession(new(CaptionPoint), items, uiModules: new(runtime, ModuleSlot));
        using (first) using (firstPeer) using (second) using (secondPeer)
        {
            await SnapshotAll(first, firstPeer); await SnapshotAll(second, secondPeer);
            await first.ActivateAsync(); await DataPlaneReceiveAsync(firstPeer);
            var original = store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0)!;
            var loop = first.RunReceiveLoopAsync().AsTask();
            var pending = runtime.DispatchAsync(0, original.Activation, 1).AsTask(); var request = await DataPlaneReceiveAsync(firstPeer);
            AssertEqual(SidecarMessageType.CommandRequest, request.MessageType);
            await second.ActivateAsync(); await DataPlaneReceiveAsync(secondPeer);
            AssertFalse((await pending.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);
            var cancellation = await DataPlaneReceiveAsync(firstPeer); AssertEqual(SidecarMessageType.Cancel, cancellation.MessageType);
            AssertEqual(request.CorrelationId, cancellation.CorrelationId);
            AssertFalse((await runtime.DispatchAsync(0, original.Activation, 1)).IsSuccess);
            second.Dispose();
            var restored = store.Read<XsrUiModuleSnapshot>(state).Value!.ModuleAt(0)!;
            AssertTrue(restored.Activation != original.Activation);
            AssertFalse((await runtime.DispatchAsync(0, original.Activation, 1)).IsSuccess);
            using var stop = new CancellationTokenSource();
            var sourcePending = runtime.DispatchAsync(0, restored.Activation, 1, cancellationToken: stop.Token).AsTask();
            await DataPlaneReceiveAsync(firstPeer); stop.Cancel();
            AssertFalse((await sourcePending.WaitAsync(TimeSpan.FromSeconds(3))).IsSuccess);
            AssertEqual(SidecarMessageType.Cancel, (await DataPlaneReceiveAsync(firstPeer)).MessageType);
            first.Dispose(); await loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
