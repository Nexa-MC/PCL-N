using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId CaptionPoint = XsrSemanticId.Parse("ui.resource.project-title.v1");

    private static async ValueTask FunctionPatchesExecuteFivePhasesLocally()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        var point = runtime.Resolve(CaptionPoint);
        var declarations = new[]
        {
            PatchItem("patch.head", XsrFunctionPatchPhase.Head, [.. Constant("H"), 6, 8]),
            PatchItem("patch.args", XsrFunctionPatchPhase.Args, [.. Constant("A"), 5, 8]),
            PatchItem("patch.replace", XsrFunctionPatchPhase.Replace, [1, .. Constant(":R"), 4, 6, 7, 8]),
            PatchItem("patch.tail", XsrFunctionPatchPhase.Tail, [2, .. Constant(":T"), 4, 6, 8]),
            PatchItem("patch.return", XsrFunctionPatchPhase.Return, [2, .. Constant(":Z"), 4, 6, 8]),
        };
        var (session, plugin) = await PatchSession(runtime, declarations);
        using (session) using (plugin)
        {
            int calls = 0;
            string Original(string input) { calls++; return input + ":O"; }
            AssertEqual("input:O", runtime.Invoke(point, "input", Original));
            await SnapshotAll(session, plugin);
            AssertEqual("input:O", runtime.Invoke(point, "input", Original));
            await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            int before = calls;
            AssertEqual("A:R:T:Z", runtime.Invoke(point, "input", Original));
            AssertEqual(before, calls);
            // No request bytes were emitted: the next wire frame is the explicit DEACTIVATE.
            await session.DeactivateAsync();
            AssertEqual(SidecarMessageType.Deactivate, (await DataPlaneReceiveAsync(plugin)).MessageType);
            AssertEqual("input:O", runtime.Invoke(point, "input", Original));
            await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            AssertEqual("A:R:T:Z", runtime.Invoke(point, "input", Original));
            session.Dispose();
            AssertEqual("input:O", runtime.Invoke(point, "input", Original));
        }
    }

    private static async ValueTask FunctionPatchesShortCircuitPreserveExceptionsAndReentry()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        var point = runtime.Resolve(CaptionPoint);
        var (session, plugin) = await PatchSession(runtime,
            [PatchItem("patch.args", XsrFunctionPatchPhase.Args, [.. Constant("changed"), 5, 8]),
             PatchItem("patch.tail", XsrFunctionPatchPhase.Tail, [2, .. Constant(":tail"), 4, 6, 8])]);
        using (session) using (plugin)
        {
            await SnapshotAll(session, plugin); await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            Exception expected = new InvalidOperationException("host body failure");
            try { runtime.Invoke(point, "original", _ => throw expected); throw new InvalidOperationException("Expected host exception."); }
            catch (Exception actual) when (ReferenceEquals(expected, actual)) { }
            AssertEqual("changed:inner:tail", runtime.Invoke(point, "original",
                value => runtime.Invoke(point, value, inner => inner + ":inner")));
            using var stop = new CancellationTokenSource();
            AssertThrows<OperationCanceledException>(() => runtime.Invoke(point, "original", value =>
            { stop.Cancel(); return value; }, stop.Token));
            AssertEqual("changed:tail", runtime.Invoke(point, "original", static value => value));
        }
        var (head, peer) = await PatchSession(runtime,
            [PatchItem("patch.head", XsrFunctionPatchPhase.Head, [.. Constant("head"), 6, 7, 8]),
             PatchItem("patch.replace", XsrFunctionPatchPhase.Replace, [.. Constant("replace"), 6, 7, 8])]);
        using (head) using (peer)
        {
            await SnapshotAll(head, peer); await head.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertEqual("head", runtime.Invoke(point, "original", _ => throw new InvalidOperationException("Must skip body.")));
        }
    }

    private static async ValueTask FunctionPatchFaultsRollbackAndResetOnReactivation()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        var point = runtime.Resolve(CaptionPoint);
        string suffix = new('x', 1024);
        var item = PatchItem("patch.args", XsrFunctionPatchPhase.Args,
            [.. Constant("partial"), 5, 1, .. Constant(suffix), 4, .. Constant(suffix), 4, 5, 8]);
        var (session, plugin) = await PatchSession(runtime, [item]);
        using (session) using (plugin)
        {
            await SnapshotAll(session, plugin); await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            // First argument store cannot leak when a later instruction exceeds the string budget.
            AssertEqual("original", runtime.Invoke(point, "original", static value => value));
            AssertEqual("small", runtime.Invoke(point, "small", static value => value));
            await session.DeactivateAsync(); await DataPlaneReceiveAsync(plugin);
            await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            AssertEqual("small", runtime.Invoke(point, "small", static value => value));
        }
        // A failure caused by the caller's original result is reset by a new activation.
        var (tail, peer) = await PatchSession(runtime,
            [PatchItem("patch.return", XsrFunctionPatchPhase.Return, [2, .. Constant("!"), 4, 6, 8])]);
        using (tail) using (peer)
        {
            await SnapshotAll(tail, peer); await tail.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            string large = new('y', 2048);
            AssertEqual(large, runtime.Invoke(point, "small", _ => large));
            AssertEqual("small", runtime.Invoke(point, "small", static value => value));
            await tail.DeactivateAsync(); await DataPlaneReceiveAsync(peer);
            await tail.ActivateAsync(); await DataPlaneReceiveAsync(peer);
            AssertEqual("small!", runtime.Invoke(point, "small", static value => value));
        }
    }

    private static async ValueTask FunctionPatchSnapshotSurvivesConcurrentRetirement()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        var point = runtime.Resolve(CaptionPoint);
        var (one, peerOne) = await PatchSession(runtime,
            [PatchItem("patch.one", XsrFunctionPatchPhase.Return, [2, .. Constant(":one"), 4, 6, 8])]);
        var (two, peerTwo) = await PatchSession(runtime,
            [PatchItem("patch.two", XsrFunctionPatchPhase.Return, [2, .. Constant(":two"), 4, 6, 8])]);
        using (one) using (two) using (peerOne) using (peerTwo)
        {
            await SnapshotAll(one, peerOne); await one.ActivateAsync(); await DataPlaneReceiveAsync(peerOne);
            await SnapshotAll(two, peerTwo); await two.ActivateAsync(); await DataPlaneReceiveAsync(peerTwo);
            AssertEqual("v:one:two", runtime.Invoke(point, "v", static value => value));
            using ManualResetEventSlim entered = new(), resume = new();
            var call = Task.Run(() => runtime.Invoke(point, "v", value => { entered.Set(); resume.Wait(); return value; }));
            AssertTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            try { one.Dispose(); }
            finally { resume.Set(); }
            AssertEqual("v:two", await call.WaitAsync(TimeSpan.FromSeconds(5)));
            AssertEqual("v:two", runtime.Invoke(point, "v", static value => value));
            two.Dispose();
            AssertEqual("v", runtime.Invoke(point, "v", static value => value));
        }
    }

    private static async ValueTask FunctionPatchValidationRejectsMalformedProgramsAndGrants()
    {
        List<byte[]> invalid =
        [
            [], "NFP2"u8.ToArray(), PatchPayload(XsrFunctionPatchPhase.Args, [5, 8]),
            PatchPayload(XsrFunctionPatchPhase.Head, [7, 8]),
            PatchPayload(XsrFunctionPatchPhase.Replace, [.. Constant("v"), 6, 8]),
            PatchPayload(XsrFunctionPatchPhase.Args, [2, 5, 8]),
            PatchPayload(XsrFunctionPatchPhase.Tail, [.. Constant("v"), 5, 8]),
            PatchPayload(XsrFunctionPatchPhase.Return, [1, 8]),
            PatchPayload(XsrFunctionPatchPhase.Head, [8, 8]),
            PatchPayload(XsrFunctionPatchPhase.Head, [255, 8]),
            PatchPayload(XsrFunctionPatchPhase.Head, [3, 1, 0, 255, 6, 8]),
            PatchPayload(XsrFunctionPatchPhase.Head, [.. Enumerable.Repeat((byte)1, 17), 8]),
        ];
        var trailing = PatchPayload(XsrFunctionPatchPhase.Head, [8]).Concat(new byte[] { 0 }).ToArray(); invalid.Add(trailing);
        var unknownAbi = PatchPayload(XsrFunctionPatchPhase.Head, [8]); unknownAbi[4] = 2; invalid.Add(unknownAbi);
        var count = PatchPayload(XsrFunctionPatchPhase.Head, [8]); count[6] = 65; invalid.Add(count);
        foreach (byte[] bytes in invalid)
        {
            XsrFunctionPatchRuntime runtime = new(CaptionPoint);
            await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(runtime,
                [PatchBytes("patch.invalid", bytes)]).AsTask());
            AssertEqual("v", runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
        }
        XsrFunctionPatchRuntime granted = new(CaptionPoint);
        var good = PatchItem("patch.good", XsrFunctionPatchPhase.Head, [8]);
        foreach (var denied in new[] { good with { Flags = 1 }, good with { TargetSemanticId = "account.ownership" } })
            await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(granted, [good, denied with { SemanticId = "patch.denied" }]).AsTask());
        await AssertThrowsAsync<SidecarProtocolException>(() => PatchSession(granted, [good], grant: false).AsTask());
        XsrSemanticId[] targets = [CaptionPoint];
        var admission = new XsrFunctionPatchAdmission(granted, targets); targets[0] = XsrSemanticId.Parse("account.ownership");
        var (accepted, peer) = await PatchSession(granted, [good], admission: admission);
        accepted.Dispose(); peer.Dispose();
        AssertThrows<ArgumentException>(() => granted.Invoke(new XsrFunctionPatchRuntime(CaptionPoint).Resolve(CaptionPoint), "v", static value => value));
    }

    private static async ValueTask FunctionPatchBudgetsAreSharedAndActivationIsAtomic()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        List<byte> operations = [];
        for (int i = 0; i < 15; i++) { operations.Add(2); operations.AddRange(Constant("x")); operations.Add(4); operations.Add(6); }
        operations.Add(8);
        var items = Enumerable.Range(0, 32).Select(i => PatchItem("patch.budget" + i,
            XsrFunctionPatchPhase.Return, operations.ToArray())).ToArray();
        var (session, plugin) = await PatchSession(runtime, items);
        using (session) using (plugin)
        {
            await SnapshotAll(session, plugin); await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            AssertEqual("v" + new string('x', 120), runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
            var (excess, peer) = await PatchSession(runtime, [PatchItem("patch.excess", XsrFunctionPatchPhase.Head, [8])]);
            using (excess) using (peer)
            {
                await SnapshotAll(excess, peer);
                await AssertThrowsAsync<SidecarProtocolException>(() => excess.ActivateAsync().AsTask());
                AssertEqual(SidecarSessionState.Failed, excess.State);
                AssertEqual("v" + new string('x', 120), runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
            }
        }
        AssertEqual("v", runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
    }

    private static async ValueTask FunctionPatchTerminalPathsRetirePrograms()
    {
        // CommandRequest exercises the invalid peer-to-Host direction.
        foreach (var ending in new[] { SidecarMessageType.Shutdown, SidecarMessageType.Crash, SidecarMessageType.CommandRequest })
        {
            XsrFunctionPatchRuntime runtime = new(CaptionPoint);
            var (session, plugin) = await PatchSession(runtime,
                [PatchItem("patch.return", XsrFunctionPatchPhase.Return, [.. Constant("patched"), 6, 8])]);
            using (session) using (plugin)
            {
                await SnapshotAll(session, plugin); await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
                AssertEqual("patched", runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
                Task receive = session.RunReceiveLoopAsync().AsTask();
                await plugin.SendAsync(new(SidecarProtocol.Version, ending, SidecarFrameTraits.Final,
                    SidecarCorrelationId.Create(), Array.Empty<byte>()));
                await receive.WaitAsync(TimeSpan.FromSeconds(5));
                AssertEqual("v", runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
            }
        }
    }

    private static async ValueTask FunctionPatchNoChangeHotPathReusesBuffers()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        var point = runtime.Resolve(CaptionPoint);
        Func<string, string> original = static value => value;
        var (session, plugin) = await PatchSession(runtime, [PatchItem("patch.noop", XsrFunctionPatchPhase.Head, [8])]);
        using (session) using (plugin)
        {
            await SnapshotAll(session, plugin); await session.ActivateAsync(); await DataPlaneReceiveAsync(plugin);
            for (int i = 0; i < 200_000; i++) _ = runtime.Invoke(point, "value", original);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) _ = runtime.Invoke(point, "value", original);
            AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    private static async ValueTask FunctionPatchBufferedActivationCannotResurrectAfterDisposal()
    {
        XsrFunctionPatchRuntime runtime = new(CaptionPoint);
        BlockingPatchWrite? stream = null;
        var (session, plugin) = await PatchSession(runtime,
            [PatchItem("patch.return", XsrFunctionPatchPhase.Return, [.. Constant("patched"), 6, 8])],
            wrap: inner => stream = new(inner));
        using (session) using (plugin)
        {
            await SnapshotAll(session, plugin);
            stream!.Blocked = true;
            Task activation = session.ActivateAsync().AsTask();
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { session.Dispose(); }
            finally { stream.Release.TrySetResult(); }
            try { await activation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or InvalidOperationException or SidecarProtocolException) { }
            AssertEqual(SidecarSessionState.Closed, session.State);
            AssertEqual("v", runtime.Invoke(runtime.Resolve(CaptionPoint), "v", static value => value));
        }
    }

    private static byte[] Constant(string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value);
        byte[] encoded = new byte[3 + text.Length]; encoded[0] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(1), checked((ushort)text.Length));
        text.CopyTo(encoded, 3); return encoded;
    }
    private static SidecarRegistrationItem PatchItem(string id, XsrFunctionPatchPhase phase, byte[] operations) =>
        PatchBytes(id, PatchPayload(phase, operations));
    private static SidecarRegistrationItem PatchBytes(string id, byte[] bytes) =>
        new(SidecarRegistrationKind.FunctionPatch, id, 0, 0, bytes, SHA256.HashData(bytes), TargetSemanticId: CaptionPoint.Value);
    private static byte[] PatchPayload(XsrFunctionPatchPhase phase, byte[] operations)
    {
        int count = 0;
        for (int i = 0; i < operations.Length; i++)
        {
            count++;
            if (operations[i] == 3 && i + 2 < operations.Length)
                i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(operations.AsSpan(i + 1));
        }
        byte[] bytes = new byte[8 + operations.Length]; "NFP1"u8.CopyTo(bytes); bytes[4] = 1; bytes[5] = (byte)phase;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), checked((ushort)count)); operations.CopyTo(bytes, 8);
        return bytes;
    }
    private static async ValueTask<(SidecarHostSession Session, SidecarConnection Plugin)> PatchSession(
        XsrFunctionPatchRuntime runtime, SidecarRegistrationItem[] items, bool grant = true,
        XsrFunctionPatchAdmission? admission = null, Func<Stream, Stream>? wrap = null,
        XsrSignalAdmission? signals = null, XsrUiPatchAdmission? uiPatches = null, XsrUiModuleAdmission? uiModules = null, SidecarFeatures features = SidecarFeatures.None)
    {
        var (hostStream, pluginStream) = SidecarLoopbackStream.CreatePair();
        SidecarConnection plugin = new(pluginStream);
        SidecarHostSession session = new(new SidecarConnection(wrap?.Invoke(hostStream) ?? hostStream), "PatchFixture")
        { FunctionPatchAdmission = admission ?? new XsrFunctionPatchAdmission(runtime, grant ? [CaptionPoint] : []), SignalAdmission = signals, UiPatchAdmission = uiPatches, UiModuleAdmission = uiModules };
        try
        {
            var handshake = session.HandshakeAsync(); var hello = await DataPlaneReceiveAsync(plugin);
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.Welcome, SidecarFrameTraits.None,
                hello.CorrelationId, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid(), null, features)));
            await handshake;
            var registration = session.AcceptRegistrationAsync();
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterBegin, SidecarFrameTraits.None,
                SidecarCorrelationId.Create(), SidecarRegistration.EncodeBegin((uint)items.Length)));
            foreach (var item in items) await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterItem,
                SidecarFrameTraits.None, SidecarCorrelationId.Create(), SidecarRegistration.EncodeItem(item)));
            await plugin.SendAsync(new(SidecarProtocol.Version, SidecarMessageType.RegisterEnd, SidecarFrameTraits.Final,
                SidecarCorrelationId.Create(), SidecarRegistration.EncodeEnd()));
            await registration;
            return (session, plugin);
        }
        catch { session.Dispose(); plugin.Dispose(); throw; }
    }

    private sealed class BlockingPatchWrite(Stream inner) : Stream
    {
        public bool Blocked { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Blocked) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
