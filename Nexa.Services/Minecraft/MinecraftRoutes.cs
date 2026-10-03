using Nexa.Services.Minecraft.Crash;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft;

public static class MinecraftCommands
{
    public static XsrCommandHandler<MinecraftStartCommand> CreateStartHandler(
        MinecraftLaunchCoordinator coordinator) =>
        async (command, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(coordinator);
            if (command.ServerAddress is { } server)
                return await coordinator.StartServerAsync(command.InstanceId, command.AccountIndex, command.MinecraftRootDirectory, server, cancellationToken).ConfigureAwait(false);
            return command.MinecraftRootDirectory is { } root
                ? await coordinator.StartAsync(command.InstanceId, command.AccountIndex, root, cancellationToken).ConfigureAwait(false)
                : await coordinator.StartAsync(command.InstanceId, command.AccountIndex, cancellationToken).ConfigureAwait(false);
        };

    public static XsrCommandHandler<MinecraftLaunchCommand> CreateLaunchHandler(Process.MinecraftProcessService processService) =>
        CreateLaunchHandler(new Launch.MinecraftLaunchExecutor(processService));

    public static XsrCommandHandler<MinecraftLaunchCommand> CreateLaunchHandler(Launch.MinecraftLaunchExecutor executor) =>
        async (command, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(executor);
            try
            {
                MinecraftLaunchPlan plan = MinecraftLaunchPlanner.CreatePlan(command.Request);
                await executor.ExecuteAsync(plan, command.Request.VersionId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return XsrResult.Success();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return XsrResult.Failure(MinecraftErrors.LaunchFailed(exception.Message));
            }
        };

    public static XsrCommandHandler<MinecraftCancelProcessCommand> CreateCancelProcessHandler(Process.MinecraftProcessService processService) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(processService);
            return ValueTask.FromResult(processService.TryCancel(command.SessionId)
                ? XsrResult.Success()
                : XsrResult.Failure(MinecraftErrors.ProcessNotFound(command.SessionId)));
        };

    public static XsrCommandHandler<MinecraftCancelLaunchCommand> CreateCancelLaunchHandler(
        Launch.MinecraftLaunchCoordinator coordinator) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(coordinator);
            return ValueTask.FromResult(coordinator.CancelActiveLaunch()
                ? XsrResult.Success()
                : XsrResult.Failure(MinecraftErrors.InvalidRequest("no launch pipeline is running.")));
        };

    public static XsrCommandHandler<MinecraftDecideJavaAcquisitionCommand> CreateAcquireDecideHandler(
        Launch.MinecraftLaunchCoordinator coordinator) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(coordinator);
            return ValueTask.FromResult(coordinator.DecideJavaAcquisition(command.Approve)
                ? XsrResult.Success()
                : XsrResult.Failure(MinecraftErrors.InvalidRequest("no Java acquisition is pending.")));
        };
}

public static class MinecraftQueries
{
    public static XsrQueryHandler<MinecraftVersionsQuery, IReadOnlyList<MinecraftVersionDescriptor>> CreateVersionsHandler(MinecraftVersionDiscovery discovery) =>
        (query, _) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(discovery);
            try { return ValueTask.FromResult(XsrResult.Success<IReadOnlyList<MinecraftVersionDescriptor>>(discovery.Discover(query.MinecraftRootDirectory, CancellationToken.None))); }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            { return ValueTask.FromResult(XsrResult.Failure<IReadOnlyList<MinecraftVersionDescriptor>>(MinecraftErrors.InvalidRequest(exception.Message))); }
        };

    public static XsrQueryHandler<MinecraftCrashAnalyzeQuery, MinecraftLaunchFaultReport> CreateCrashHandler() =>
        (query, _) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            MinecraftLaunchFaultReport report = MinecraftLaunchFaultAnalyzer.AnalyzeText(query.Evidence, query.Stage, query.LastClassName);
            return ValueTask.FromResult(XsrResult.Success(report));
        };

    public static XsrQueryHandler<MinecraftInstancesQuery, IReadOnlyList<MinecraftInstanceDescriptor>> CreateInstancesHandler(MinecraftInstanceDiscovery discovery) =>
        async (query, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(discovery);
            try { return XsrResult.Success<IReadOnlyList<MinecraftInstanceDescriptor>>(await discovery.DiscoverAsync(query.MinecraftRootDirectory, cancellationToken).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            { return XsrResult.Failure<IReadOnlyList<MinecraftInstanceDescriptor>>(MinecraftErrors.InvalidRequest(exception.Message)); }
        };
}
