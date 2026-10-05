using Nexa.Sidecar.Protocol;
using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime;

/// <summary>Explicit Host access to admitted Sidecars, with sealed numeric XSR routes.</summary>
public sealed class SidecarHostApi
{
    private readonly SidecarSupervisor _supervisor;

    public SidecarHostApi(SidecarSupervisor supervisor, IXsrDispatchObserver? observer = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        observer ??= SilentObserver.Instance;
        XsrCommandRouterBuilder commands = new();
        commands.Register<SidecarCommandCall>(SidecarHostRoutes.Command,
            (call, token) => SendCommandAsync(call, token));
        commands.Register<SidecarBinaryCall>(SidecarHostRoutes.BinaryCommand, async (call, token) =>
        {
            var result = await SendBinaryCommandAsync(call, token).ConfigureAwait(false);
            return result.IsSuccess ? XsrResult.Success() : XsrResult.Failure(result.Error!);
        });
        commands.Register<SidecarPackageCall>(SidecarHostRoutes.Stop,
            (call, token) => StopAsync(call.PackageName, token));
        commands.Register<SidecarPackageCall>(SidecarHostRoutes.Restart,
            (call, token) => RestartAsync(call.PackageName, token));
        commands.Register<SidecarReloadCall>(SidecarHostRoutes.Reload,
            (_, token) => ReloadAsync(token));
        Commands = commands.Build(observer);

        XsrQueryRouterBuilder queries = new();
        queries.Register<SidecarCatalogQuery, IReadOnlyList<SidecarPackageSnapshot>>(SidecarHostRoutes.Catalog,
            (_, _) => ValueTask.FromResult(XsrResult.Success(_supervisor.PackageSnapshots)));
        queries.Register<SidecarQueryCall, string>(SidecarHostRoutes.Query, SendQueryAsync);
        queries.Register<SidecarBinaryCall, SidecarBinaryValue>(SidecarHostRoutes.BinaryQuery, SendBinaryQueryAsync);
        queries.Register<SidecarContractCall, SidecarStateRead>(SidecarHostRoutes.State,
            (call, token) => ValueTask.FromResult(token.IsCancellationRequested
                ? XsrResult.Failure<SidecarStateRead>(XsrRuntimeErrors.Cancelled()) : ReadState(call)));
        queries.Register<SidecarContractCall, SidecarCachedContent>(SidecarHostRoutes.Resource,
            (call, token) => ValueTask.FromResult(token.IsCancellationRequested
                ? XsrResult.Failure<SidecarCachedContent>(XsrRuntimeErrors.Cancelled()) : ReadResource(call)));
        queries.Register<SidecarContractCall, SidecarCachedContent>(SidecarHostRoutes.UiModule,
            (call, token) => ValueTask.FromResult(token.IsCancellationRequested
                ? XsrResult.Failure<SidecarCachedContent>(XsrRuntimeErrors.Cancelled()) : ReadUiModule(call)));
        queries.Register<SidecarHealthCall, TimeSpan>(SidecarHostRoutes.Health, PingAsync);
        queries.Register<SidecarBinaryCall, SidecarHostStream>(SidecarHostRoutes.Stream, OpenStreamAsync);
        queries.Register<SidecarPackageCall, SidecarSessionInfo>(SidecarHostRoutes.Session,
            (call, token) => ValueTask.FromResult(token.IsCancellationRequested
                ? XsrResult.Failure<SidecarSessionInfo>(XsrRuntimeErrors.Cancelled()) : ReadSession(call.PackageName)));
        Queries = queries.Build(observer);
    }

    public XsrCommandRouter Commands { get; }
    public XsrQueryRouter Queries { get; }
    public IReadOnlyList<SidecarPackageSnapshot> PackageSnapshots => _supervisor.PackageSnapshots;

    public ValueTask<XsrResult> SendCommandAsync(SidecarCommandCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure(error))
            : session!.SendCommandAsync(call.Contract, call.Argument, call.Timeout, token);

    public ValueTask<XsrResult<string>> SendQueryAsync(SidecarQueryCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure<string>(error))
            : session!.SendQueryAsync(call.Contract, call.Argument, call.Timeout, token);

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryCommandAsync(SidecarBinaryCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure<SidecarBinaryValue>(error))
            : session!.SendBinaryCommandAsync(call.Contract, call.Argument, call.Timeout, token);

    public ValueTask<XsrResult<SidecarBinaryValue>> SendBinaryQueryAsync(SidecarBinaryCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure<SidecarBinaryValue>(error))
            : session!.SendBinaryQueryAsync(call.Contract, call.Argument, call.Timeout, token);

    public ValueTask<XsrResult<TimeSpan>> PingAsync(SidecarHealthCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure<TimeSpan>(error))
            : session!.PingAsync(call.Timeout, token);

    public ValueTask<XsrResult<SidecarHostStream>> OpenStreamAsync(SidecarBinaryCall call, CancellationToken token = default) =>
        Resolve(call.PackageName, out var session, token) is { } error
            ? ValueTask.FromResult(XsrResult.Failure<SidecarHostStream>(error))
            : session!.OpenStreamAsync(call.Contract, call.Argument, call.Timeout, token);

    public XsrResult<SidecarSessionInfo> ReadSession(string packageName)
    {
        if (Resolve(packageName, out var session) is { } error) return XsrResult.Failure<SidecarSessionInfo>(error);
        return XsrResult.Success(new SidecarSessionInfo(session!.SessionId, session.State, session.NegotiatedFeatures,
            session.Registration!.Entries, session.ReadMetrics()));
    }

    /// <summary>Reads a coherent typed cell locally; this method sends no wire message.</summary>
    public XsrResult<SidecarStateRead> ReadState(SidecarContractCall call)
    {
        if (Resolve(call.PackageName, out var session) is { } error) return XsrResult.Failure<SidecarStateRead>(error);
        var entry = session!.Registration?.TryResolve(SidecarRegistrationKind.State, call.Contract);
        if (entry is null || session.Mirror is not { } mirror || mirror.TryResolve(call.Contract) is not { } id)
            return XsrResult.Failure<SidecarStateRead>(XsrRuntimeErrors.RouteNotFound());
        var store = mirror.Store;
        return entry.CodecId switch
        {
            SidecarWireCodecs.Utf8String => State(store.Read<string>(id), entry.CodecId),
            SidecarWireCodecs.Bool => State(store.Read<bool>(id), entry.CodecId),
            SidecarWireCodecs.I32 => State(store.Read<int>(id), entry.CodecId),
            SidecarWireCodecs.I64 => State(store.Read<long>(id), entry.CodecId),
            SidecarWireCodecs.F64 => State(store.Read<double>(id), entry.CodecId),
            SidecarWireCodecs.Bytes or SidecarWireCodecs.GeneratedDto => State(store.Read<byte[]>(id), entry.CodecId),
            _ => XsrResult.Failure<SidecarStateRead>(XsrRuntimeErrors.ContractMismatch()),
        };
    }

    public XsrResult<SidecarCachedContent> ReadResource(SidecarContractCall call)
    {
        if (Resolve(call.PackageName, out var session) is { } error) return XsrResult.Failure<SidecarCachedContent>(error);
        return session!.Registration?.TryResolve(SidecarRegistrationKind.Resource, call.Contract) is not null
            && session.Cache.TryGetResource(call.Contract, out var bytes)
            ? XsrResult.Success(new SidecarCachedContent(bytes!))
            : XsrResult.Failure<SidecarCachedContent>(XsrRuntimeErrors.RouteNotFound());
    }

    public XsrResult<SidecarCachedContent> ReadUiModule(SidecarContractCall call)
    {
        if (Resolve(call.PackageName, out var session) is { } error) return XsrResult.Failure<SidecarCachedContent>(error);
        return session!.Registration?.TryResolve(SidecarRegistrationKind.UiModule, call.Contract) is not null
            && session.Cache.TryOpenUiModule(call.Contract, out var bytes)
            ? XsrResult.Success(new SidecarCachedContent(bytes!))
            : XsrResult.Failure<SidecarCachedContent>(XsrRuntimeErrors.RouteNotFound());
    }

    public ValueTask<XsrResult> StopAsync(string packageName, CancellationToken token = default) =>
        ControlAsync(() => _supervisor.StopAsync(packageName, token),
            XsrRuntimeErrors.TargetNotFound("The Sidecar package is not admitted."), token);

    public ValueTask<XsrResult> RestartAsync(string packageName, CancellationToken token = default) =>
        ControlAsync(() => _supervisor.RestartAsync(packageName, token), Unavailable(), token);

    public async ValueTask<XsrResult> ReloadAsync(CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await _supervisor.ReloadAsync(token).ConfigureAwait(false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) { return XsrResult.Failure(token.IsCancellationRequested ? XsrRuntimeErrors.Cancelled() : Unavailable()); }
        catch (ObjectDisposedException) { return XsrResult.Failure(Unavailable()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { return XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted()); }
    }

    private XsrError? Resolve(string packageName, out SidecarHostSession? session, CancellationToken token = default)
    {
        session = null;
        if (token.IsCancellationRequested) return XsrRuntimeErrors.Cancelled();
        if (_supervisor.TryGetSession(packageName, out session) && session is not null) return null;
        return _supervisor.PackageSnapshots.Any(package => string.Equals(package.PackageName, packageName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            ? new(XsrErrorKind.Unavailable, XsrSemanticId.Parse("xsr.unavailable"), "The Sidecar package is not active.")
            : XsrRuntimeErrors.TargetNotFound("The Sidecar package is not admitted.");
    }

    private static XsrError Unavailable() => new(XsrErrorKind.Unavailable,
        XsrSemanticId.Parse("xsr.unavailable"), "The Sidecar supervisor is not accepting this operation.");

    private static async ValueTask<XsrResult> ControlAsync(Func<ValueTask<bool>> operation, XsrError notApplied, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false) ? XsrResult.Success() : XsrResult.Failure(notApplied);
        }
        catch (OperationCanceledException) { return XsrResult.Failure(token.IsCancellationRequested ? XsrRuntimeErrors.Cancelled() : Unavailable()); }
        catch (ObjectDisposedException) { return XsrResult.Failure(Unavailable()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { return XsrResult.Failure(XsrRuntimeErrors.HandlerFaulted()); }
    }

    private static XsrResult<SidecarStateRead> State<T>(XsrStateValue<T> value, uint codecId) =>
        XsrResult.Success(new SidecarStateRead(value.Revision, value.Availability, value.HasValue,
            value.HasValue ? new(codecId, SidecarWireCodecs.Encode(codecId, value.Value!)) : null));

    private sealed class SilentObserver : IXsrDispatchObserver
    {
        public static readonly SilentObserver Instance = new();
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
}
