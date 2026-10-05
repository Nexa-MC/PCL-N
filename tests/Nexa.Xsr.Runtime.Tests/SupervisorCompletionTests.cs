using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly XsrSemanticId CompleteCaptionTarget = XsrSemanticId.Parse("fixture.host.caption");
    private static readonly XsrSemanticId CompleteCardTarget = XsrSemanticId.Parse("fixture.host.card");
    private static readonly XsrSemanticId CompleteIntentTarget = XsrSemanticId.Parse("fixture.host.intent");

    private static async ValueTask PhysicalSidecarHostApiCompletesLifecycle()
    {
        using var fixture = new CompleteSupervisorFixture();
        string package = await fixture.AddPackageAsync("provider");
        await fixture.AddPackageAsync("unsigned", signed: false);
        var builder = new XsrStateStoreBuilder();
        var captionsKey = XsrSemanticId.Parse("fixture.captions");
        var modulesKey = XsrSemanticId.Parse("fixture.modules");
        builder.Cell<XsrUiPatchSnapshot>(captionsKey, "Fixture");
        builder.Cell<XsrUiModuleSnapshot>(modulesKey, "Fixture");
        var store = builder.Build();
        var captions = new XsrUiPatchRuntime(store, store.Resolve(captionsKey), new XsrUiCaptionTarget(CompleteCaptionTarget, 16));
        var modules = new XsrUiModuleRuntime(store, store.Resolve(modulesKey), CompleteCardTarget);
        var signals = new XsrSignalRuntime(new XsrSignalDefinition(CompleteIntentTarget, XsrSignalKind.Intent, AllowsCatch: true));
        var functions = new XsrFunctionPatchRuntime(CaptionPoint);
        await using var supervisor = new SidecarSupervisor(fixture.VerifyAsync)
        {
            UiPatchAdmission = new(captions, CompleteCaptionTarget),
            UiModuleAdmission = new(modules, CompleteCardTarget),
            SignalAdmission = new(signals, CompleteIntentTarget),
            FunctionPatchAdmission = new(functions, CaptionPoint),
        };
        SidecarHostApi api = new(supervisor);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await supervisor.StartAsync(fixture.DirectoryPath, deadline.Token);
        AssertTrue(supervisor.TryGetSession(package, out var session));
        var original = session!;
        var observer = new CompleteEventObserver();
        original.AttachEventObserver(observer);
        var catalog = await CompleteQuery<SidecarCatalogQuery, IReadOnlyList<SidecarPackageSnapshot>>(api,
            SidecarHostRoutes.Catalog, new());
        AssertEqual(2, catalog.Value.Count);
        AssertEqual(SidecarPackageStatus.Failed, catalog.Value.Single(item => item.PackageName == "unsigned").Status);
        var info = await CompleteQuery<SidecarPackageCall, SidecarSessionInfo>(api, SidecarHostRoutes.Session, new(package));
        AssertEqual(SidecarFeatures.All, info.Value.Features);
        var initial = await CompleteQuery<SidecarContractCall, SidecarStateRead>(api, SidecarHostRoutes.State,
            new(package, XsrSemanticId.Parse("fixture.pid")));
        AssertEqual(original.SessionId, info.Value.SessionId);
        AssertEqual(fixture.ProcessId(package), (int)SidecarWireCodecs.Decode(initial.Value.Value!.CodecId, initial.Value.Value.Span));
        AssertEqual("Extension", store.Read<XsrUiPatchSnapshot>(store.Resolve(captionsKey)).Value!.CaptionAt(captions.Resolve(CompleteCaptionTarget))!);
        AssertEqual("Fixture card", store.Read<XsrUiModuleSnapshot>(store.Resolve(modulesKey)).Value!.CardAt(modules.Resolve(CompleteCardTarget))!.Title);
        AssertEqual("original:plugin", functions.Invoke(functions.Resolve(CaptionPoint), "original", static value => value));

        long localReadFrames = original.ReadMetrics().SentFrames;
        var resource = await CompleteQuery<SidecarContractCall, SidecarCachedContent>(api, SidecarHostRoutes.Resource,
            new(package, XsrSemanticId.Parse("fixture.resource")));
        AssertTrue(resource.Value.Span.SequenceEqual("resource"u8));
        var card = await CompleteQuery<SidecarContractCall, SidecarCachedContent>(api, SidecarHostRoutes.UiModule,
            new(package, XsrSemanticId.Parse("fixture.card")));
        AssertEqual("Fixture card", SidecarUiCard.Decode(card.Value.Span).Title);
        AssertEqual(localReadFrames, original.ReadMetrics().SentFrames);
        var status = await CompleteQuery<SidecarQueryCall, string>(api, SidecarHostRoutes.Query,
            new(package, XsrSemanticId.Parse("fixture.status")));
        AssertEqual("ready", status.Value);
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.Command,
            new SidecarCommandCall(package, XsrSemanticId.Parse("fixture.execute")))).IsSuccess);
        await observer.Executed.Task.WaitAsync(deadline.Token);
        var changed = await CompleteQuery<SidecarContractCall, SidecarStateRead>(api, SidecarHostRoutes.State,
            new(package, XsrSemanticId.Parse("fixture.state")));
        AssertEqual("updated", (string)SidecarWireCodecs.Decode(changed.Value.Value!.CodecId, changed.Value.Value.Span));
        AssertTrue(changed.Value.Revision >= 2);
        var binaryCall = new SidecarBinaryCall(package, XsrSemanticId.Parse("fixture.binary"), CompleteInt(41));
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.BinaryCommand, binaryCall)).IsSuccess);
        var binary = await api.SendBinaryCommandAsync(binaryCall);
        AssertEqual(42, (int)SidecarWireCodecs.Decode(binary.Value.CodecId, binary.Value.Span));
        var binaryQuery = await CompleteQuery<SidecarBinaryCall, SidecarBinaryValue>(api, SidecarHostRoutes.BinaryQuery,
            new(package, XsrSemanticId.Parse("fixture.binary-status"), CompleteInt(10)));
        AssertEqual(20L, (long)SidecarWireCodecs.Decode(binaryQuery.Value.CodecId, binaryQuery.Value.Span));
        var health = await CompleteQuery<SidecarHealthCall, TimeSpan>(api, SidecarHostRoutes.Health, new(package));
        AssertTrue(health.IsSuccess);
        AssertTrue(health.Value >= TimeSpan.Zero);
        var streamResult = await CompleteQuery<SidecarBinaryCall, SidecarHostStream>(api, SidecarHostRoutes.Stream,
            new(package, XsrSemanticId.Parse("fixture.stream"), CompleteInt(3)));
        await using (var stream = streamResult.Value)
        {
            var numbers = new List<int>();
            await foreach (var value in stream.ReadAllAsync(deadline.Token)) numbers.Add((int)SidecarWireCodecs.Decode(value.CodecId, value.Span));
            AssertTrue(numbers.SequenceEqual(Enumerable.Range(3, 20)));
            AssertTrue((await stream.Completion).IsSuccess);
        }

        using var cancel = new CancellationTokenSource();
        var waiting = CompleteCommand(api, SidecarHostRoutes.Command,
            new SidecarCommandCall(package, XsrSemanticId.Parse("fixture.cancel")), cancel.Token);
        await observer.Waiting.Task.WaitAsync(deadline.Token);
        cancel.Cancel();
        AssertEqual("xsr.cancelled", (await waiting).Error!.Code.Value);
        await observer.Cancelled.Task.WaitAsync(deadline.Token);
        AssertTrue(signals.Emit(signals.Resolve(CompleteIntentTarget), "clicked"));
        await observer.Signal.Task.WaitAsync(deadline.Token);

        await original.DeactivateAsync(deadline.Token);
        AssertTrue(store.Read<XsrUiPatchSnapshot>(store.Resolve(captionsKey)).Value!.CaptionAt(captions.Resolve(CompleteCaptionTarget)) is null);
        AssertEqual("original", functions.Invoke(functions.Resolve(CaptionPoint), "original", static value => value));
        var inactive = await CompleteQuery<SidecarQueryCall, string>(api, SidecarHostRoutes.Query,
            new(package, XsrSemanticId.Parse("fixture.status")));
        AssertEqual("xsr.unavailable", inactive.Error!.Code.Value);
        await original.ActivateAsync(deadline.Token);
        AssertEqual("original:plugin", functions.Invoke(functions.Resolve(CaptionPoint), "original", static value => value));
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.Restart, new SidecarPackageCall(package))).IsSuccess);
        AssertTrue(supervisor.TryGetSession(package, out var replacement));
        AssertTrue(replacement!.SessionId != original.SessionId);
        AssertEqual(SidecarSessionState.Closed, original.State);
        AssertEqual(XsrStateAvailability.Unavailable, original.Mirror!.Store.Read<int>(original.Mirror.TryResolve(XsrSemanticId.Parse("fixture.pid"))!.Value).Availability);
        AssertEqual(2, fixture.VerificationCount(package));
        AssertTrue(File.Exists(fixture.LifecyclePath(package, "shutdown")));
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.Stop, new SidecarPackageCall(package))).IsSuccess);
        AssertFalse(supervisor.TryGetSession(package, out _));
        AssertEqual(SidecarPackageStatus.Stopped, supervisor.PackageSnapshots.Single(item => item.PackageName == package).Status);
        if (OperatingSystem.IsWindows())
        {
            var stopped = await api.SendQueryAsync(new(package.ToUpperInvariant(), XsrSemanticId.Parse("fixture.status")));
            AssertEqual("xsr.unavailable", stopped.Error!.Code.Value);
        }
        AssertEqual(0, supervisor.Sessions.Count);
        AssertFalse(signals.Emit(signals.Resolve(CompleteIntentTarget), "clicked"));
        AssertTrue(store.Read<XsrUiModuleSnapshot>(store.Resolve(modulesKey)).Value!.CardAt(modules.Resolve(CompleteCardTarget)) is null);
        AssertTrue(File.Exists(fixture.LifecyclePath(package, "unregister")));
        await supervisor.ShutdownAsync(deadline.Token);
        await supervisor.ShutdownAsync(deadline.Token);
        AssertFalse(supervisor.TryGetSession(package, out _));
        AssertTrue((await CompleteQuery<SidecarPackageCall, SidecarSessionInfo>(api,
            SidecarHostRoutes.Session, new(package))).Error!.Code.Value == "xsr.unavailable");
    }

    private static async ValueTask PhysicalSidecarRecoveryIsBoundedAndIsolated()
    {
        using var fixture = new CompleteSupervisorFixture();
        await fixture.AddPackageAsync("healthy");
        await fixture.AddPackageAsync("loop");
        await using var supervisor = new SidecarSupervisor(fixture.VerifyAsync);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await supervisor.StartAsync(fixture.DirectoryPath, deadline.Token);
        await CompleteWaitUntil(() => supervisor.PackageSnapshots.Single(item => item.PackageName == "loop").Status
            == SidecarPackageStatus.Quarantined, deadline.Token);
        AssertEqual(4, fixture.VerificationCount("loop"));
        AssertEqual(3, supervisor.PackageSnapshots.Single(item => item.PackageName == "loop").RecoveryAttempts);
        AssertTrue(supervisor.TryGetSession("healthy", out var healthy));
        SidecarHostApi api = new(supervisor);
        AssertEqual("ready", (await CompleteQuery<SidecarQueryCall, string>(api, SidecarHostRoutes.Query,
            new("healthy", XsrSemanticId.Parse("fixture.status")))).Value);
        File.Delete(Path.Combine(fixture.DirectoryPath, "loop.crash"));
        AssertTrue(await supervisor.RestartAsync("loop", deadline.Token));
        AssertEqual(0, supervisor.PackageSnapshots.Single(item => item.PackageName == "loop").RecoveryAttempts);
        AssertTrue(supervisor.TryGetSession("loop", out var recovered));
        AssertTrue(ReferenceEquals(healthy, supervisor.Sessions.Single(item => item.PluginName == "healthy")));
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.Command,
            new SidecarCommandCall("loop", XsrSemanticId.Parse("fixture.crash")))).Error is not null);
        await CompleteWaitUntil(() => supervisor.TryGetSession("loop", out var fresh)
            && fresh!.SessionId != recovered!.SessionId, deadline.Token);
        AssertEqual(6, fixture.VerificationCount("loop"));
        AssertTrue(supervisor.TryGetSession("healthy", out _));
        await supervisor.ShutdownAsync(deadline.Token);
        AssertEqual(0, supervisor.Sessions.Count);
    }

    private static async ValueTask PhysicalSidecarReloadReverifiesAdmittedPackages()
    {
        using var fixture = new CompleteSupervisorFixture();
        await fixture.AddPackageAsync("provider");
        await using var supervisor = new SidecarSupervisor(fixture.VerifyAsync);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await supervisor.StartAsync(fixture.DirectoryPath, deadline.Token);
        AssertTrue(supervisor.TryGetSession("provider", out var original));
        await File.WriteAllBytesAsync(fixture.ImagePath("provider") + ".asc", new byte[32], deadline.Token);
        AssertFalse(await supervisor.RestartAsync("provider", deadline.Token));
        AssertFalse(supervisor.TryGetSession("provider", out _));
        AssertEqual(SidecarSessionState.Closed, original!.State);
        await fixture.SignAsync("provider");
        AssertTrue(await supervisor.RestartAsync("provider", deadline.Token));
        AssertFalse(await supervisor.RestartAsync("../provider", deadline.Token));
        AssertFalse(await supervisor.StopAsync(fixture.ImagePath("provider"), deadline.Token));
        await supervisor.StopAsync("provider", deadline.Token);
        File.Delete(fixture.ImagePath("provider"));
        File.Delete(fixture.ImagePath("provider") + ".asc");
        await fixture.AddPackageAsync("replacement");
        SidecarHostApi api = new(supervisor);
        AssertTrue((await CompleteCommand(api, SidecarHostRoutes.Reload, new SidecarReloadCall())).IsSuccess);
        AssertEqual(1, supervisor.PackageSnapshots.Count);
        AssertTrue(supervisor.TryGetSession("replacement", out _));
        AssertFalse(supervisor.TryGetSession("provider", out _));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => supervisor.RestartAsync("replacement", cancelled.Token).AsTask());
        AssertTrue(supervisor.TryGetSession("replacement", out _));
        await Task.WhenAll(supervisor.RestartAsync("replacement", deadline.Token).AsTask(), supervisor.StopAsync("replacement", deadline.Token).AsTask());
        AssertFalse(supervisor.TryGetSession("replacement", out _));
        await supervisor.ShutdownAsync(deadline.Token);
        await AssertThrowsAsync<Exception>(() => supervisor.ReloadAsync(deadline.Token).AsTask());
        AssertFalse(supervisor.TryGetSession("replacement", out _));
    }

    private static async ValueTask SupervisorDisposalCancelsUnpublishedStartup()
    {
        using var fixture = new CompleteSupervisorFixture();
        await fixture.AddPackageAsync("provider");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new SidecarSupervisor(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Task startup = supervisor.StartAsync(fixture.DirectoryPath);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await supervisor.ShutdownAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await AssertThrowsAsync<OperationCanceledException>(() => startup);
        AssertEqual(0, supervisor.Sessions.Count);
        AssertFalse(supervisor.TryGetSession("provider", out _));
        AssertEqual(SidecarPackageStatus.Stopped, supervisor.PackageSnapshots.Single().Status);
        await AssertThrowsAsync<Exception>(() => supervisor.RestartAsync("provider").AsTask());
        AssertFalse(supervisor.TryGetSession("provider", out _));
    }

    private static async ValueTask<XsrResult<T>> CompleteQuery<TQuery, T>(SidecarHostApi api, XsrSemanticId route, TQuery query)
        where TQuery : notnull
    {
        AssertTrue(api.Queries.TryResolve(route, out var id));
        return await api.Queries.QueryAsync<TQuery, T>(id, query);
    }

    private static async Task<XsrResult> CompleteCommand<T>(SidecarHostApi api, XsrSemanticId route, T command,
        CancellationToken cancellationToken = default) where T : notnull
    {
        AssertTrue(api.Commands.TryResolve(route, out var id));
        var dispatch = api.Commands.Dispatch(id, command, cancellationToken: cancellationToken);
        AssertTrue(dispatch.Acceptance.IsSuccess);
        return await dispatch.Completion;
    }

    private static SidecarBinaryValue CompleteInt(int value) => new(SidecarWireCodecs.I32, SidecarWireCodecs.Encode(SidecarWireCodecs.I32, value));

    // The independent execution engine remains in Nexa.Plugin. This is a test peer reached
    // only through a private fixture marker in the copied harness directory.
    private static async Task<int> RunCompleteSupervisorChildAsync(string endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using Stream stream = await SidecarBootstrap.ConnectAsync(endpoint, Console.OpenStandardInput(), deadline.Token);
        using var connection = new SidecarConnection(stream);
        var hello = await connection.ReceiveAsync(deadline.Token);
        AssertEqual(SidecarMessageType.Hello, hello.MessageType);
        var details = SidecarHandshake.DecodeHelloDetails(hello.Payload.Span);
        string package = details.PeerName;
        string PhasePath(string phase) => Path.Combine(AppContext.BaseDirectory, package + "." + phase);
        File.WriteAllText(PhasePath("pid"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ValueTask Send(SidecarMessageType type, byte[] payload, SidecarCorrelationId correlation = default) =>
            connection.SendAsync(new(SidecarProtocol.Version, type, SidecarFrameTraits.None,
                correlation.IsAssigned ? correlation : SidecarCorrelationId.Create(), payload), deadline.Token);
        await Send(SidecarMessageType.Welcome,
            SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid(), null, details.Features & SidecarFeatures.All), hello.CorrelationId);
        byte[] card = new SidecarUiCard(CompleteCardTarget.Value, "Fixture card", "Local, literal, and session-owned.").Encode();
        byte[] resource = "resource"u8.ToArray();
        byte[] caption = SidecarUiCaptionPatch.Encode("Extension");
        byte[] caught = [.. "NXS1"u8.ToArray(), 1];
        var declarations = new SidecarRegistrationItem[]
        {
            new(SidecarRegistrationKind.Command, "fixture.execute", 0, 0),
            new(SidecarRegistrationKind.Command, "fixture.cancel", 0, 0),
            new(SidecarRegistrationKind.Command, "fixture.crash", 0, 0),
            new(SidecarRegistrationKind.Command, "fixture.binary", 0, SidecarWireCodecs.I32) { ResultCodecId = SidecarWireCodecs.I32 },
            new(SidecarRegistrationKind.Query, "fixture.status", 0, 0),
            new(SidecarRegistrationKind.Query, "fixture.binary-status", 0, SidecarWireCodecs.I32) { ResultCodecId = SidecarWireCodecs.I64 },
            new(SidecarRegistrationKind.State, "fixture.pid", 0, SidecarWireCodecs.I32),
            new(SidecarRegistrationKind.State, "fixture.state", 0, 0),
            new(SidecarRegistrationKind.Event, "fixture.event", 0, 0),
            new(SidecarRegistrationKind.Stream, "fixture.stream", 0, SidecarWireCodecs.I32) { ResultCodecId = SidecarWireCodecs.I32 },
            new(SidecarRegistrationKind.Resource, "fixture.resource", 0, 0, resource, SHA256.HashData(resource)),
        };
        // The lifecycle/recovery fixtures do not grant presentation execution; declarations
        // are still hashed and retained without accidentally granting plugin authority.
        if (package == "provider")
        {
            declarations = [.. declarations,
                new(SidecarRegistrationKind.UiModule, "fixture.card", 0, 0, card, SHA256.HashData(card)),
                new(SidecarRegistrationKind.UiPatch, "fixture.caption", 0, 0, caption, SHA256.HashData(caption), TargetSemanticId: CompleteCaptionTarget.Value),
                new(SidecarRegistrationKind.IntentCatch, "fixture.intent", 0, 0, caught, SHA256.HashData(caught), TargetSemanticId: CompleteIntentTarget.Value),
                PatchItem("fixture.patch", XsrFunctionPatchPhase.Return, [2, .. Constant(":plugin"), 4, 6, 8])];
        }
        await Send(SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin((uint)declarations.Length));
        foreach (var declaration in declarations) await Send(SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(declaration));
        await Send(SidecarMessageType.RegisterEnd, SidecarRegistration.EncodeEnd());
        await Send(SidecarMessageType.StateSnapshotBegin, SidecarStateSnapshot.EncodeBegin(2));
        await Send(SidecarMessageType.StateSnapshotItem,
            SidecarStateSnapshot.EncodeItem(1, SidecarWireCodecs.Encode(SidecarWireCodecs.I32, Environment.ProcessId)));
        await Send(SidecarMessageType.StateSnapshotItem,
            SidecarStateSnapshot.EncodeItem(2, SidecarWireCodecs.Encode(SidecarWireCodecs.Utf8String, "initial")));
        await Send(SidecarMessageType.StateSnapshotEnd, SidecarStateSnapshot.EncodeEnd());
        AssertEqual(SidecarMessageType.Ready, (await connection.ReceiveAsync(deadline.Token)).MessageType);
        bool active = false, unregistered = false;
        var cancelledCommands = new HashSet<Guid>();
        var streams = new Dictionary<Guid, CompletePeerStream>();
        async ValueTask PumpStream(SidecarCorrelationId correlation, CompletePeerStream pending)
        {
            while (pending.Credit > 0 && pending.Remaining > 0)
            {
                await Send(SidecarMessageType.StreamChunk,
                    SidecarStreamMessages.EncodeChunk(pending.Sequence++, CompleteInt(pending.Value++)), correlation);
                pending.Credit--; pending.Remaining--;
            }
            if (pending.Remaining == 0)
            {
                await Send(SidecarMessageType.StreamEnd, SidecarStreamMessages.EncodeEnd(pending.Sequence, true, null), correlation);
                streams.Remove(correlation.Value);
            }
        }
        while (true)
        {
            SidecarFrame frame;
            try { frame = await connection.ReceiveAsync(deadline.Token); }
            catch (IOException) { return 0; }
            switch (frame.MessageType)
            {
                case SidecarMessageType.Activate:
                    AssertFalse(unregistered);
                    active = true;
                    File.WriteAllText(PhasePath("activate"), "fixture");
                    if (File.Exists(PhasePath("crash"))) return 33;
                    break;
                case SidecarMessageType.Deactivate:
                    AssertTrue(active);
                    active = false;
                    streams.Clear(); cancelledCommands.Clear();
                    File.WriteAllText(PhasePath("deactivate"), "fixture");
                    break;
                case SidecarMessageType.CommandRequest:
                    {
                        AssertTrue(active);
                        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
                        switch (request.ContractId)
                        {
                            case 1:
                                await Send(SidecarMessageType.StateDelta, SidecarDataMessages.EncodeStateDelta(2,
                                    SidecarWireCodecs.Encode(SidecarWireCodecs.Utf8String, "updated")));
                                await Send(SidecarMessageType.Event, SidecarDataMessages.EncodeEvent(1, "executed"));
                                await Send(SidecarMessageType.CommandResult, SidecarDataMessages.EncodeResult(true, "", null), frame.CorrelationId);
                                break;
                            case 2:
                                cancelledCommands.Add(frame.CorrelationId.Value);
                                await Send(SidecarMessageType.Event, SidecarDataMessages.EncodeEvent(1, "waiting"));
                                break;
                            case 3: return 34;
                            case 4:
                                int value = (int)SidecarWireCodecs.Decode(request.Value.CodecId, request.Value.Span);
                                await Send(SidecarMessageType.CommandResult,
                                    SidecarDataMessages.EncodeBinaryResult(true, CompleteInt(value + 1), null), frame.CorrelationId);
                                break;
                            default: throw new InvalidOperationException("Unknown fixture command.");
                        }
                        break;
                    }
                case SidecarMessageType.QueryRequest:
                    {
                        AssertTrue(active);
                        var request = SidecarDataMessages.DecodeBinaryRequest(frame.Payload.Span);
                        if (request.ContractId == 1)
                            await Send(SidecarMessageType.QueryResult, SidecarDataMessages.EncodeResult(true, "ready", null), frame.CorrelationId);
                        else
                        {
                            AssertEqual(2U, request.ContractId);
                            int value = (int)SidecarWireCodecs.Decode(request.Value.CodecId, request.Value.Span);
                            var result = new SidecarBinaryValue(SidecarWireCodecs.I64, SidecarWireCodecs.Encode(SidecarWireCodecs.I64, (long)value * 2));
                            await Send(SidecarMessageType.QueryResult, SidecarDataMessages.EncodeBinaryResult(true, result, null), frame.CorrelationId);
                        }
                        break;
                    }
                case SidecarMessageType.HealthPing:
                    await Send(SidecarMessageType.HealthPong, SidecarControlMessages.EncodeHealth(
                        SidecarControlMessages.DecodeHealth(frame.Payload.Span)), frame.CorrelationId);
                    break;
                case SidecarMessageType.StreamOpen:
                    {
                        AssertTrue(active);
                        var request = SidecarStreamMessages.DecodeOpen(frame.Payload.Span);
                        AssertEqual(1U, request.ContractId);
                        var openedStream = new CompletePeerStream((int)SidecarWireCodecs.Decode(request.Argument.CodecId, request.Argument.Span), request.InitialCredit);
                        streams.Add(frame.CorrelationId.Value, openedStream);
                        await PumpStream(frame.CorrelationId, openedStream);
                        break;
                    }
                case SidecarMessageType.StreamCredit:
                    if (streams.TryGetValue(frame.CorrelationId.Value, out var pending))
                    {
                        pending.Credit += SidecarStreamMessages.DecodeCredit(frame.Payload.Span);
                        AssertTrue(pending.Credit <= SidecarStreamMessages.MaximumCredit);
                        await PumpStream(frame.CorrelationId, pending);
                    }
                    break;
                case SidecarMessageType.Cancel:
                    {
                        var cancelled = SidecarStateSnapshot.DecodeCancel(frame.Payload.Span);
                        if (cancelledCommands.Remove(cancelled.CorrelationId))
                            await Send(SidecarMessageType.Event, SidecarDataMessages.EncodeEvent(1, "cancelled"));
                        streams.Remove(cancelled.CorrelationId);
                        break;
                    }
                case SidecarMessageType.HookSignal:
                    AssertTrue(active);
                    var signal = SidecarHookSignal.Decode(frame.Payload.Span);
                    AssertEqual(SidecarRegistrationKind.IntentCatch, signal.Kind);
                    AssertEqual("clicked", signal.Value);
                    await Send(SidecarMessageType.Event, SidecarDataMessages.EncodeEvent(1, "signal"));
                    break;
                case SidecarMessageType.Unregister:
                    AssertFalse(active);
                    unregistered = true;
                    File.WriteAllText(PhasePath("unregister"), "fixture");
                    await Send(SidecarMessageType.Unregistered,
                        SidecarControlMessages.EncodeUnregister(SidecarControlMessages.DecodeUnregister(frame.Payload.Span)), frame.CorrelationId);
                    break;
                case SidecarMessageType.Shutdown:
                    AssertFalse(active);
                    AssertTrue(unregistered);
                    File.WriteAllText(PhasePath("shutdown"), "fixture");
                    return 0;
                default: throw new InvalidOperationException("Unexpected fixture control.");
            }
        }
    }

    private sealed class CompletePeerStream(int value, uint credit)
    {
        public int Value { get; set; } = value;
        public uint Credit { get; set; } = credit;
        public ulong Sequence { get; set; } = 1;
        public int Remaining { get; set; } = 20;
    }

    private static async Task CompleteWaitUntil(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(20, token);
    }

    private sealed class CompleteEventObserver : ISidecarSessionEventObserver
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnEvent(XsrSemanticId semanticId, string payload)
        {
            if (semanticId.Value != "fixture.event") return;
            switch (payload)
            {
                case "executed": Executed.TrySetResult(); break;
                case "waiting": Waiting.TrySetResult(); break;
                case "cancelled": Cancelled.TrySetResult(); break;
                case "signal": Signal.TrySetResult(); break;
            }
        }
    }

    private sealed class CompleteSupervisorFixture : IDisposable
    {
        private readonly Dictionary<string, int> _verified = new(StringComparer.Ordinal);
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "nexa-sidecar-full-" + Guid.NewGuid().ToString("N"));
        public CompleteSupervisorFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(DirectoryPath, "sidecar-full.fixture"), "fixture");
        }
        public string ImagePath(string package) => Path.Combine(DirectoryPath, package + ".nsc");
        public string LifecyclePath(string package, string phase) => Path.Combine(DirectoryPath, package + "." + phase);
        public int ProcessId(string package) => int.Parse(File.ReadAllText(Path.Combine(DirectoryPath, package + ".pid")), System.Globalization.CultureInfo.InvariantCulture);
        public async Task<string> AddPackageAsync(string package, bool signed = true)
        {
            string nativeHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Nexa.Xsr.Runtime.Tests.exe" : "Nexa.Xsr.Runtime.Tests");
            File.Copy(nativeHost, ImagePath(package));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ImagePath(package), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (signed) await SignAsync(package);
            if (package == "loop") File.WriteAllText(Path.Combine(DirectoryPath, package + ".crash"), "fixture");
            return package;
        }
        public async Task SignAsync(string package) => await File.WriteAllBytesAsync(ImagePath(package) + ".asc", SHA256.HashData(await File.ReadAllBytesAsync(ImagePath(package))));
        public Task VerifyAsync(Stream image, Stream signature, CancellationToken token) => VerifyCoreAsync(image, signature, token);
        private async Task VerifyCoreAsync(Stream image, Stream signature, CancellationToken token)
        {
            byte[] actual = await SHA256.HashDataAsync(image, token);
            byte[] expected = new byte[32];
            await signature.ReadExactlyAsync(expected, token);
            if (!actual.SequenceEqual(expected)) throw new InvalidDataException("Fixture signature mismatch.");
            string package = Path.GetFileNameWithoutExtension(((FileStream)image).Name);
            lock (_verified) _verified[package] = _verified.GetValueOrDefault(package) + 1;
        }
        public int VerificationCount(string package) { lock (_verified) return _verified.GetValueOrDefault(package); }
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
