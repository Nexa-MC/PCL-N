using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Services.Setup;
using Nexa.Services.Telemetry;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

/// <summary>
/// The composed foundation runtime: the foundation host (services over one shared state
/// store) plus the XSR command and query routers with every foundation route registered.
/// This is the only place foundation services meet the runtime dispatch layer; the product
/// never calls foundation service methods directly when an intent can be a command.
/// </summary>
public sealed class FoundationRuntime
{
    public FoundationRuntime(FoundationHost host, XsrCommandRouter commands, XsrQueryRouter queries)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public FoundationHost Host { get; }

    public XsrCommandRouter Commands { get; }

    public XsrQueryRouter Queries { get; }
}

/// <summary>
/// Builds the foundation runtime over an existing host: registers every foundation route
/// into fresh command/query routers and seals them.
/// </summary>
public static class FoundationRuntimeComposer
{
    private sealed class NullDispatchObserver : IXsrDispatchObserver
    {
        public static readonly NullDispatchObserver Instance = new();

        public void OnCompleted(XsrDispatchObservation observation)
        {
        }
    }

    public static FoundationRuntime Compose(
        FoundationHost host,
        IXsrDispatchObserver? observer = null,
        TimeProvider? timeProvider = null) => ComposeCore(host, observer, timeProvider, null);

    public static FoundationRuntime ComposeWithStorage(
        FoundationHost host,
        StoragePreferencesService storagePreferences,
        IXsrDispatchObserver? observer = null,
        TimeProvider? timeProvider = null) => ComposeCore(host, observer, timeProvider,
            storagePreferences ?? throw new ArgumentNullException(nameof(storagePreferences)));

    public static FoundationRuntime ComposeWithStorageAndMedia(
        FoundationHost host, StoragePreferencesService storagePreferences,
        Func<ReadOnlyMemory<byte>, int, int, int, int, byte[]> screenshotCrop,
        IXsrDispatchObserver? observer = null, TimeProvider? timeProvider = null,
        Action<XsrCommandRouterBuilder, XsrQueryRouterBuilder>? configureRoutes = null,
        Func<string, CancellationToken, Task>? captureWorld = null, Func<string, IDisposable>? acquireWorldSessionLock = null) =>
        ComposeCore(host, observer, timeProvider, storagePreferences, screenshotCrop, configureRoutes, captureWorld, acquireWorldSessionLock);

    private static FoundationRuntime ComposeCore(
        FoundationHost host,
        IXsrDispatchObserver? observer,
        TimeProvider? timeProvider,
        StoragePreferencesService? storagePreferences,
        Func<ReadOnlyMemory<byte>, int, int, int, int, byte[]>? screenshotCrop = null,
        Action<XsrCommandRouterBuilder, XsrQueryRouterBuilder>? configureRoutes = null,
        Func<string, CancellationToken, Task>? captureWorld = null, Func<string, IDisposable>? acquireWorldSessionLock = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        IXsrDispatchObserver dispatchObserver = observer ?? NullDispatchObserver.Instance;

        XsrCommandRouterBuilder commands = new();
        XsrQueryRouterBuilder queries = new();
        if (storagePreferences is not null)
        {
            commands.Register<StorageMigrationCommand>(StoragePreferencesContract.Migrate,
                async (command, token) => await storagePreferences.QueueMigrationAsync(command, token).ConfigureAwait(false));
            commands.Register<StorageMigrationCancelCommand>(StoragePreferencesContract.CancelMigration,
                async (command, token) => await storagePreferences.CancelQueuedMigrationAsync(command, token).ConfigureAwait(false));
            commands.Register<StorageCleanupCommand>(StoragePreferencesContract.Cleanup,
                async (command, token) => await storagePreferences.CleanupAsync(command, token).ConfigureAwait(false));
        }
        var recovery = new InstanceRecoveryService(host.SettingsPolicy, host.StateStore, host.Logging);
        var exporter = new InstanceModpackExportService(host.Tasks, host.StateStore);
        commands.Register<InstanceModpackExportCommand>(InstanceModpackExportContract.Export,
            async (command, token) => await exporter.ExportAsync(command, token).ConfigureAwait(false));
        commands.Register<InstanceServerListSaveCommand>(InstanceServerListContract.Save,
            async (command, token) => await InstanceServerListService.SaveAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceRecoveryRestoreCommand>(InstanceRecoveryContract.Restore,
            async (command, token) => await recovery.RestoreAsync(command, token).ConfigureAwait(false));
        commands.Register<InstanceRecoveryResumeCommand>(InstanceRecoveryContract.Recover,
            async (command, token) => await recovery.RecoverAsync(command, token).ConfigureAwait(false));
        commands.Register<InstanceModEnabledCommand>(InstanceManagementContract.SetModEnabled,
            async (command, token) => await InstanceContentService.SetModEnabledAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceContentRemoveCommand>(InstanceManagementContract.RemoveContent,
            async (command, token) => await InstanceContentTrash.RemoveAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceModRemovalCommand>(InstanceManagementContract.RemoveMod,
            async (command, token) => await InstanceModRemovalService.RemoveAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceContentRestoreCommand>(InstanceManagementContract.RestoreContent,
            async (command, token) => await InstanceContentTrash.RestoreAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register(
            FoundationRouteIds.SettingsSet,
            FoundationCommands.CreateSettingsSetHandler(host.Settings));
        commands.Register(
            FoundationRouteIds.TelemetryConsent,
            FoundationCommands.CreateTelemetryConsentHandler(host.Telemetry));
        commands.Register(
            FoundationRouteIds.AccountUpsertProfile,
            FoundationCommands.CreateAccountUpsertHandler(host.Accounts));
        commands.Register(
            FoundationRouteIds.AccountSelectProfile,
            FoundationCommands.CreateAccountSelectHandler(host.Accounts));
        commands.Register(FoundationRouteIds.AccountRemoveProfile, FoundationCommands.CreateAccountRemoveHandler(host.Accounts));
        commands.Register<SettingsMutation>(SettingsPolicyContract.SetCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.Set(command), token)));
        commands.Register<SettingsBatchCommand>(SettingsPolicyContract.BatchCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.SetBatch(command), token)));
        commands.Register<SettingsImportCommand>(SettingsPolicyContract.ImportCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.ApplyImport(command), token)));
        commands.Register<SettingsResetCommand>(SettingsPolicyContract.ResetCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.ApplyReset(command), token)));
        SettingsLaunchProfileRuntime.Register(commands, queries, host.SettingsPolicy);
        commands.Register<MachineCapabilityRefresh>(MachineCapabilityStateContract.RefreshCommand, async (command, token) =>
        {
            await host.MachineCapabilities.ReadAsync(refresh: true, cancellationToken: token).ConfigureAwait(false);
            return Nexa.Xsr.XsrResult.Success();
        });
        commands.Register<RemediationRequest>(MachineCapabilityStateContract.RemediationCommand, async (request, token) =>
        {
            RemediationResult result = await host.Remediations.ExecuteAsync(request, token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                await host.MachineCapabilities.ReadAsync(refresh: true, cancellationToken: token)
                    .ConfigureAwait(false);
            }
            return result.Succeeded
                ? Nexa.Xsr.XsrResult.Success()
                : Nexa.Xsr.XsrResult.Failure(new Nexa.Xsr.XsrError(
                    Nexa.Xsr.XsrErrorKind.Rejected,
                    Nexa.Xsr.XsrSemanticId.Parse("machine.capabilities.remediation.rejected"),
                    result.Message));
        });
        var javaManagement = new Nexa.Services.Minecraft.Java.JavaRuntimeManagementService(host.JavaLocator, host.JavaRegistrations, host.JavaManagedRuntimeRoots);
        commands.Register<Nexa.Services.Minecraft.Java.JavaRuntimeManageCommand>(Nexa.Services.Minecraft.Java.JavaRuntimeInventoryContract.Manage, javaManagement.ManageAsync);
        ContentManagementRuntime.Register(commands, queries, host.StateStore, captureWorld, acquireWorldSessionLock);
        InstanceIdentityRuntime.Register(commands, queries);
        InstanceOfflineReadinessRuntime.Register(queries, host.JavaLocator, host.SettingsPolicy);
        if (screenshotCrop is not null)
            commands.Register<InstanceScreenshotCropCommand>(InstanceScreenshotContract.Crop,
                (command, token) => new(InstanceScreenshotService.CropAsync(command, host.StateStore, screenshotCrop, token)));
        if (storagePreferences is not null)
        {
            queries.Register<StoragePreferencesQuery, StoragePreferencesStatus>(StoragePreferencesContract.Status,
                (_, _) => ValueTask.FromResult(storagePreferences.ReadStatus()));
            queries.Register<StorageMigrationQuery, StorageMigrationPreview>(StoragePreferencesContract.MigrationPreview,
                async (query, token) => await storagePreferences.PreviewMigrationAsync(query, token).ConfigureAwait(false));
            queries.Register<StorageCleanupQuery, StorageCleanupPreview>(StoragePreferencesContract.CleanupPreview,
                async (query, token) => await storagePreferences.PreviewCleanupAsync(query, token).ConfigureAwait(false));
        }
        JavaDiagnosticsRuntime.Register(queries, host);
        var javaInventory = new Nexa.Services.Minecraft.Java.JavaRuntimeInventoryService(host.JavaLocator, host.JavaRegistrations, host.JavaManagedRuntimeRoots);
        queries.Register<Nexa.Services.Minecraft.Java.JavaRuntimeInventoryQuery, Nexa.Services.Minecraft.Java.JavaRuntimeInventorySnapshot>(
            Nexa.Services.Minecraft.Java.JavaRuntimeInventoryContract.Query, javaInventory.ReadAsync);
        queries.Register<InstanceModpackExportQuery, InstanceModpackExportPreview>(InstanceModpackExportContract.Preview,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await InstanceModpackExportService.PreviewAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceServerListQuery, InstanceServerList>(InstanceServerListContract.Read,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await InstanceServerListService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceServerStatusQuery, InstanceServerStatus>(InstanceServerListContract.Status,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await InstanceServerStatusService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceModRemovalQuery, InstanceModRemovalPreview>(InstanceManagementContract.ModRemovalPreview,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await InstanceModRemovalService.PreviewAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query,
            async (query, token) =>
            {
                var snapshot = await InstanceManagementService.ReadAsync(query, host.SharedStateCache, token).ConfigureAwait(false);
                if (query.IncludeRecoveryStorage)
                {
                    var comparison = await recovery.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
                    snapshot = snapshot with
                    {
                        RecoveryComparison = comparison.IsSuccess ? comparison.Value :
                        new(query.InstanceDirectory, null, null, [], "", "暂时无法比较更改，请刷新重试。")
                    };
                }
                return Nexa.Xsr.XsrResult.Success(snapshot);
            });
        queries.Register<InstanceRecoveryQuery, InstanceRecoveryReport>(InstanceRecoveryContract.Query,
            async (query, token) => await recovery.ReadAsync(query, token).ConfigureAwait(false));
        queries.Register(
            FoundationRouteIds.SettingsGet,
            FoundationQueries.CreateSettingsGetHandler(host.Settings));
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(SettingsCatalog.Read(query))));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Read(query)));
        queries.Register<SettingsPreviewQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.PreviewQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Preview(query)));
        queries.Register<SettingsExportQuery, string>(SettingsPolicyContract.ExportQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Export(query)));
        queries.Register<SettingsImportQuery, SettingsImportPreview>(SettingsPolicyContract.ImportPreviewQuery,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(host.SettingsPolicy.PreviewImport(query))));
        queries.Register<SettingsResetQuery, SettingsResetPreview>(SettingsPolicyContract.ResetPreviewQuery,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(host.SettingsPolicy.PreviewReset(query))));
        queries.Register<MachineCapabilityQuery, MachineCapabilitySnapshot>(MachineCapabilityStateContract.SnapshotQuery,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await host.MachineCapabilities.ReadAsync(query, cancellationToken: token).ConfigureAwait(false)));
        queries.Register<LaunchPreflightQuery, CapabilityPreflightReport>(MachineCapabilityStateContract.PreflightQuery,
            (query, token) =>
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(query.Snapshot);
                return ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(
                    CapabilityPreflightEngine.Evaluate(query.Snapshot)));
            });
        queries.Register<Nexa.Services.Network.NetworkTraceQuery, IReadOnlyList<Nexa.Services.Network.NetworkRequestTrace>>(
            Nexa.Services.Network.NetworkDiagnosticsContract.Trace,
            (_, _) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(host.NetworkHttp.TraceSnapshot())));
        var networkProbe = new Nexa.Services.Network.NetworkManualProbeService(() => host.CreateHttpClient(false));
        queries.Register<Nexa.Services.Network.NetworkManualProbeQuery, Nexa.Services.Network.NetworkManualProbeSnapshot>(
            Nexa.Services.Network.NetworkDiagnosticsContract.Probe,
            async (_, token) => Nexa.Xsr.XsrResult.Success(await networkProbe.ProbeAsync(token).ConfigureAwait(false)));
        configureRoutes?.Invoke(commands, queries);
        XsrCommandRouter commandRouter = commands.Build(dispatchObserver, timeProvider);
        XsrQueryRouter queryRouter = queries.Build(dispatchObserver, timeProvider);

        return new FoundationRuntime(host, commandRouter, queryRouter);
    }
}
