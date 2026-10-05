using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>
/// Executes a prepared launch in the required order: validate artifacts, stage native archives,
/// then hand the immutable plan to the process boundary.
/// </summary>
public sealed class MinecraftLaunchExecutor
{
    private readonly IJvmHost _jvmHost;
    private readonly LogService? _log;
    private readonly IMinecraftLaunchHookPort _hooks;

    public MinecraftLaunchExecutor(MinecraftProcessService processes, LogService? log = null)
        : this(new JvmHostService(processes), log, new SystemMinecraftLaunchHookPort())
    {
    }

    public MinecraftLaunchExecutor(IJvmHost jvmHost, LogService? log = null)
        : this(jvmHost, log, new SystemMinecraftLaunchHookPort())
    {
    }

    public MinecraftLaunchExecutor(MinecraftProcessService processes, LogService? log, IMinecraftLaunchHookPort hooks)
        : this(new JvmHostService(processes), log, hooks)
    {
    }

    public MinecraftLaunchExecutor(IJvmHost jvmHost, LogService? log, IMinecraftLaunchHookPort hooks)
    {
        _jvmHost = jvmHost ?? throw new ArgumentNullException(nameof(jvmHost));
        _log = log;
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
    }

    public async ValueTask<MinecraftProcessSession> ExecuteAsync(
        MinecraftLaunchPlan plan,
        string instanceId,
        Action<string>? stage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        using LogOperation? operation = _log?.BeginOperation("Launch", "ExecuteLaunch", $"instance={instanceId}");
        IMinecraftLaunchHookSession? hook = null;
        MinecraftProcessSession? startedSession = null;
        JavaRuntimeUseLease? javaUse = null;
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            javaUse = await JavaRuntimeUseLease.AcquireAsync(plan.JavaExecutablePath, cancellationToken)
                .ConfigureAwait(false);
            _ = MinecraftLaunchHooks.ParseWrapper(plan.WrapperCommand);
            MinecraftLaunchHooks.ValidatePreLaunch(plan.PreLaunchCommand);

            operation?.Stage("validate_native_archives", $"count={plan.NativeLibraries.Count}");
            IReadOnlyList<MinecraftLibraryToken> natives = plan.NativeLibraries;
            string[] nativePaths = new string[natives.Count];
            for (int index = 0; index < natives.Count; index++)
            {
                string path = natives[index].LocalPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    _log?.Warn("Launch", $"Required native archive is missing instance={instanceId} archive={path}");
                    throw new FileNotFoundException(
                        "A native library archive required by the Minecraft launch is missing.",
                        path);
                }

                nativePaths[index] = path;
            }

            // Even a launch with no native archives gets a deterministic directory for
            // ${natives_directory}; extraction itself is idempotent and cancellation-aware.
            string nativesDirectory = string.IsNullOrWhiteSpace(plan.NativesDirectory)
                ? Path.Combine(plan.WorkingDirectory, "natives")
                : plan.NativesDirectory;
            operation?.Stage("extract_natives", $"directory={nativesDirectory}");
            await MinecraftNativesExtractor.ExtractAsync(nativePaths, nativesDirectory, cancellationToken)
                .ConfigureAwait(false);
            stage?.Invoke(MinecraftLaunchStages.PreLaunch);
            Directory.CreateDirectory(plan.WorkingDirectory);
            stage?.Invoke(MinecraftLaunchStages.CustomCommand);
            if (!string.IsNullOrWhiteSpace(plan.PreLaunchCommand))
            {
                operation?.Stage("custom_command", $"wait={plan.WaitForPreLaunchCommand}");
                try
                {
                    hook = await _hooks.StartAsync(plan.PreLaunchCommand, plan.WorkingDirectory, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception)
                { throw new IOException("The pre-launch command could not be started."); }
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.WaitForPreLaunchCommand)
                {
                    int exitCode = await hook.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    if (exitCode != 0)
                        throw new InvalidOperationException($"The pre-launch command failed with exit code {exitCode}.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            operation?.Stage("start_process");
            stage?.Invoke("start_process");
            MinecraftProcessSession session = startedSession = await _jvmHost.StartAsync(plan, instanceId, cancellationToken).ConfigureAwait(false);
            if (javaUse is { } runtimeUse)
            {
                runtimeUse.BindProcess(session.Snapshot.ProcessId);
                // The process owns this lease even if cancellation wins immediately after
                // creation. Preparation cleanup must not release a still-running JVM's use.
                _ = ReleaseJavaUseAfterExitAsync(session, runtimeUse);
                javaUse = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Before this handoff even nonwaiting hooks belong to launch preparation. A
            // cancelled or failed JVM startup therefore cannot leave that shell behind.
            if (hook is not null && !plan.WaitForPreLaunchCommand)
                hook.Detach(exitCode =>
                {
                    if (exitCode != 0) _log?.Warn("Launch", $"Detached pre-launch command failed instance={instanceId} exit_code={exitCode}.");
                });
            if (plan.ProcessPriority is { } priority)
            {
                try
                {
                    var result = _jvmHost.SetPriority(session, priority);
                    if (!result.Succeeded) _log?.Warn("Launch", $"Process priority was not applied session={session.Snapshot.SessionId} code={result.Code}.");
                }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                { _log?.Warn("Launch", $"Process priority failed session={session.Snapshot.SessionId} exception={error.GetType().Name}."); }
            }
            operation?.Complete($"session={session.Snapshot.SessionId} pid={session.Snapshot.ProcessId}");
            completed = true;
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            startedSession?.Cancel();
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            startedSession?.Cancel();
            operation?.Fail(exception);
            throw;
        }
        finally
        {
            try
            {
                if (hook is not null) await hook.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (!completed && startedSession is not null)
                        await startedSession.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally { javaUse?.Dispose(); }
            }
        }
    }

    private static async Task ReleaseJavaUseAfterExitAsync(MinecraftProcessSession session, JavaRuntimeUseLease lease)
    {
        try { await session.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { }
        finally { lease.Dispose(); }
    }
}
