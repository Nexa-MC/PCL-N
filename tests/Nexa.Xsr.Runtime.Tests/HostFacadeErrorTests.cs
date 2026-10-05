using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask HostFacadePreservesAdmissionCancellationAndShutdownErrors()
    {
        await using SidecarSupervisor supervisor = new(static (_, _, _) =>
            throw new InvalidOperationException("The empty Host fixture must never verify or launch a package."));
        SidecarHostApi api = new(supervisor);
        const string missingPackage = "unadmitted";
        XsrSemanticId semantic = XsrSemanticId.Parse("fixture.contract");
        SidecarContractCall local = new(missingPackage, semantic);
        SidecarCommandCall command = new(missingPackage, semantic);
        SidecarQueryCall query = new(missingPackage, semantic);
        SidecarBinaryCall binary = new(missingPackage, semantic, new(SidecarWireCodecs.Bytes, [1, 2, 3]));

        // Construction seals the local routes without discovery, files, or child processes.
        AssertEqual(5, api.Commands.Count);
        AssertEqual(9, api.Queries.Count);
        AssertTrue(api.Commands.TryResolve(SidecarHostRoutes.BinaryCommand, out _));
        AssertFalse(api.Queries.TryResolve(SidecarHostRoutes.BinaryCommand, out _));
        AssertTrue(api.Queries.TryResolve(SidecarHostRoutes.Catalog, out XsrQueryId catalogId));
        var catalog = await api.Queries.QueryAsync<SidecarCatalogQuery, IReadOnlyList<SidecarPackageSnapshot>>(catalogId, new());
        AssertTrue(catalog.IsSuccess);
        AssertEqual(0, catalog.Value.Count);
        foreach (XsrError? error in new[]
        {
            api.ReadSession(missingPackage).Error, api.ReadState(local).Error,
            api.ReadResource(local).Error, api.ReadUiModule(local).Error,
        })
            AssertFacadeError(error, XsrErrorKind.NotFound, "xsr.target_not_found");

        Func<CancellationToken, ValueTask<XsrError?>>[] sends =
        [
            async token => (await api.SendCommandAsync(command, token)).Error,
            async token => (await api.SendQueryAsync(query, token)).Error,
            async token => (await api.SendBinaryCommandAsync(binary, token)).Error,
            async token => (await api.SendBinaryQueryAsync(binary, token)).Error,
            async token => (await api.PingAsync(new(missingPackage), token)).Error,
            async token => (await api.OpenStreamAsync(binary, token)).Error,
        ];
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        foreach (var send in sends)
        {
            AssertFacadeError(await send(CancellationToken.None), XsrErrorKind.NotFound, "xsr.target_not_found");
            AssertFacadeError(await send(cancelled.Token), XsrErrorKind.Cancelled, "xsr.cancelled");
        }

        // Local reads are cancellable through their typed router contracts; cancellation wins
        // over missing-package lookup, just as it does for direct asynchronous calls.
        XsrError?[] cancelledReads =
        [
            await FacadeQueryError<SidecarContractCall, SidecarStateRead>(api, SidecarHostRoutes.State, local, cancelled.Token),
            await FacadeQueryError<SidecarContractCall, SidecarCachedContent>(api, SidecarHostRoutes.Resource, local, cancelled.Token),
            await FacadeQueryError<SidecarContractCall, SidecarCachedContent>(api, SidecarHostRoutes.UiModule, local, cancelled.Token),
            await FacadeQueryError<SidecarPackageCall, SidecarSessionInfo>(api, SidecarHostRoutes.Session, new(missingPackage), cancelled.Token),
        ];
        foreach (XsrError? error in cancelledReads)
            AssertFacadeError(error, XsrErrorKind.Cancelled, "xsr.cancelled");

        AssertTrue(api.Commands.TryResolve(SidecarHostRoutes.Command, out XsrCommandId commandId));
        var wrongCommand = api.Commands.Dispatch(commandId, new SidecarPackageCall(missingPackage));
        AssertFacadeError(wrongCommand.Acceptance.Error, XsrErrorKind.ContractMismatch, "xsr.contract_mismatch");
        AssertFacadeError((await wrongCommand.Completion).Error, XsrErrorKind.ContractMismatch, "xsr.contract_mismatch");
        AssertFacadeError(await FacadeQueryError<SidecarQueryCall, int>(api, SidecarHostRoutes.Query, query, CancellationToken.None),
            XsrErrorKind.ContractMismatch, "xsr.contract_mismatch");

        // Supervisor lifetime cancellation has a different owner from the caller token. A
        // closed Host must report unavailable rather than claiming the caller cancelled it.
        await supervisor.ShutdownAsync();
        Func<CancellationToken, ValueTask<XsrResult>>[] controls =
        [
            token => api.StopAsync(missingPackage, token),
            token => api.RestartAsync(missingPackage, token),
            token => api.ReloadAsync(token),
        ];
        foreach (var control in controls)
        {
            AssertFacadeError((await control(CancellationToken.None)).Error, XsrErrorKind.Unavailable, "xsr.unavailable");
            AssertFacadeError((await control(cancelled.Token)).Error, XsrErrorKind.Cancelled, "xsr.cancelled");
        }
    }

    private static async ValueTask<XsrError?> FacadeQueryError<TQuery, TResponse>(SidecarHostApi api,
        XsrSemanticId route, TQuery query, CancellationToken cancellationToken) where TQuery : notnull
    {
        AssertTrue(api.Queries.TryResolve(route, out XsrQueryId id));
        return (await api.Queries.QueryAsync<TQuery, TResponse>(id, query, cancellationToken: cancellationToken)).Error;
    }

    private static void AssertFacadeError(XsrError? error, XsrErrorKind kind, string code)
    {
        AssertTrue(error is not null);
        AssertEqual(kind, error!.Kind);
        AssertEqual(code, error.Code.Value);
    }
}
