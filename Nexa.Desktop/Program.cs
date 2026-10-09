using System.Reflection;
using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Composition;
using Nexa.Services.Files;
using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft;
using Nexa.Services.Settings;
using Nexa.Services.Setup;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop;

internal static partial class Program
{
    /// <summary>
    /// A WinExe process has a console handle only when launched from a terminal (or with
    /// redirected output); double-clicking the exe reports none.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// The version channel (alpha, beta, ci, or release) parsed from the informational version
    /// that Xsr.Version.props composes, e.g. 2.0.0.ci.1a2b3c.
    /// </summary>
    private static string? ResolveVersionChannel()
    {
        string version = ResolveInformationalVersion();
        string[] segments = version.Split('.');
        return segments.Length >= 4 && segments[3] is "alpha" or "beta" or "ci"
            ? segments[3]
            : null;
    }

    private static string ResolveInformationalVersion() => Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The one version truth: informational version, channel, and semantic core.</summary>

    /// <summary>
    /// Merges runtime account configuration with the public client IDs embedded by publish.
    /// Runtime LittleSkin configuration remains authoritative so developers can override the
    /// packaged device-flow client without rebuilding the launcher.
    /// </summary>
    private static AccountOnboardingOptions ComposeAccountOnboardingOptions()
    {
        AccountOnboardingOptions fromEnvironment = AccountOnboardingOptions.FromEnvironment();
        return MergeAccountOnboardingOptions(
            fromEnvironment,
            ResolveEmbeddedClientId("NexaMicrosoftClientId"),
            ResolveEmbeddedClientId("NexaLittleSkinClientId"));
    }

    internal static AccountOnboardingOptions MergeAccountOnboardingOptions(
        AccountOnboardingOptions runtime,
        string? embeddedMicrosoftClientId,
        string? embeddedLittleSkinClientId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        LittleSkinOAuthConfiguration? littleSkin = runtime.LittleSkin;
        if (littleSkin is null && !string.IsNullOrWhiteSpace(embeddedLittleSkinClientId))
        {
            littleSkin = new LittleSkinOAuthConfiguration(
                embeddedLittleSkinClientId.Trim(),
                string.Empty,
                new Uri(LittleSkinOAuthService.DeviceFlowRedirectUri));
        }

        return runtime with
        {
            MicrosoftClientId = !string.IsNullOrWhiteSpace(embeddedMicrosoftClientId)
                ? embeddedMicrosoftClientId.Trim()
                : runtime.MicrosoftClientId,
            LittleSkin = littleSkin,
        };
    }

    private static string? ResolveEmbeddedClientId(string key) =>
        System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;

    private static Nexa.Services.Updates.LauncherBuildIdentity ResolveBuildInfo() =>
        Nexa.Services.Updates.LauncherBuildIdentity.Parse(ResolveInformationalVersion());

    // The Win32 clipboard (Avalonia's OLE implementation) requires an STA thread with COM
    // initialized; without this, every SetTextAsync fails with CO_E_NOTINITIALIZED.
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args is [Nexa.Services.Minecraft.Process.MinecraftLaunchHookWorker.WorkerArgument])
            return await Nexa.Services.Minecraft.Process.MinecraftLaunchHookWorker.RunWorkerAsync().ConfigureAwait(false);
        if (RedirectUpdatedLauncher(args)) return 0;
        if (args is [Nexa.Services.Processes.OwnedInstallerProcess.WorkerArgument])
            return await Nexa.Services.Processes.OwnedInstallerProcess.RunWorkerAsync().ConfigureAwait(false);
        DesktopCommandLineRequest commandLine = DesktopCommandLine.Parse(args);
        if (!commandLine.IsSuccess) { Console.Error.WriteLine(commandLine.Error); return 2; }
        if (commandLine.ListCommands)
        {
            UiLocalizationCatalog listingLanguage = new(); listingLanguage.SetLanguage("auto");
            Console.WriteLine(DesktopCommandLine.FormatListing(listingLanguage.Translate)); return 0;
        }
        if (commandLine.Command is { } requestedCommand)
            args = [.. args.Where(argument => !argument.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)), "nexacl://" + requestedCommand.Id];
        LogService? log = null;
        FileLogSink? fileSink = null;
        DesktopSingleInstance? instance = null;
        AvaloniaUiStartupSession? startup = null;
        long bootstrapStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        double? mainToNativeSubmissionMs = null;
        bool guiStarted = false;
        string stage = "resolve_folders";
        int exitCode = 1;
        void OnUnhandled(object sender, UnhandledExceptionEventArgs e) => log?.Error(
            "Launcher", $"Unhandled exception terminating={e.IsTerminating} stage={stage}",
            e.ExceptionObject is Exception exception ? ExceptionDiagnostics.Describe(exception) : "Unknown exception object.");
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) =>
            log?.Error("Launcher", "Unobserved background task failure", ExceptionDiagnostics.Describe(e.Exception));
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            AppFolders folders = AppFolders.ResolveDefault();
            bool disableHardwareAcceleration = commandLine.SafeMode || HardwareAccelerationDisabled(folders.Root);
            bool validation = args.Contains("--validate-shell", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--validate-setup", StringComparer.OrdinalIgnoreCase);
            DesktopDestination destination = DesktopDestination.Activate;
            if (args.FirstOrDefault(argument => argument.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)) is { } uri
                && !DesktopActivation.TryParse(uri, out destination))
            {
                Console.Error.WriteLine("不支持的 nexacl:// 链接。");
                return 2;
            }
            if (!validation && (commandLine.SafeMode || SingleInstanceEnabled(folders.Root)))
            {
                string? activationFile = args.FirstOrDefault(argument => DesktopActivation.TryFile(argument, out _));
                instance = await DesktopSingleInstance.AcquireAsync(Path.GetDirectoryName(LauncherStorageLocation.LocatorPath)!, destination, activationFile, forwardActivation: !commandLine.SafeMode).ConfigureAwait(false);
                if (instance is null)
                {
                    if (commandLine.SafeMode)
                    { Console.Error.WriteLine("请先关闭正在运行的 NexaCL，再使用安全模式启动。"); exitCode = 2; return 2; }
                    exitCode = 0; return 0;
                }
            }
            if (!validation)
            {
                UiLocalizationCatalog startupLanguage = new();
                startupLanguage.SetLanguage("auto");
                startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync(args, disableHardwareAcceleration,
                    ReadStartupAppearance(folders.Root), localize: startupLanguage.Translate).ConfigureAwait(false);
                mainToNativeSubmissionMs = System.Diagnostics.Stopwatch.GetElapsedTime(bootstrapStarted).TotalMilliseconds;
            }
            bool locationLocked = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXA_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("PCL_NEXA_DATA_DIR"));
            stage = "complete_storage_transaction";
            startup?.ReportStage("检查数据位置");
            // Recover before opening logs, settings or any other root consumer. Await here,
            // before ApplicationSession starts its STA thread: the GUI bootstrap must not
            // resume on a thread-pool thread after asynchronous file copying.
            var storageStartup = await StoragePreferencesService.CompletePendingMigrationAsync(
                folders.Root, LauncherStorageLocation.LocatorPath, locationLocked, startup?.CancellationToken ?? default).ConfigureAwait(false);
            startup?.CancellationToken.ThrowIfCancellationRequested();
            if (storageStartup.IsSuccess) folders = AppFolders.ResolveDefault();
            else
            {
                try { Console.Error.WriteLine("存储恢复未完成，继续使用原数据位置：" + storageStartup.Error?.Message); }
                catch (IOException) { }
            }
            startup?.SetAppearance(ReadStartupAppearance(folders.Root));
            while (true)
            {
                try
                {
                    exitCode = await Nexa.Platform.ApplicationSession.RunAsync(() => RunAsync(args, logging =>
                        {
                            log = logging;
                            if (startup?.FirstRenderElapsed is { } firstRender)
                                logging.Info("Startup", FormattableString.Invariant($"first_native_render_submission_ms={firstRender.TotalMilliseconds:F2} main_to_native_submission_ms={mainToNativeSubmissionMs:F2}"));
                        },
                        sink => fileSink = sink, value =>
                        {
                            stage = value;
                            if (value is "gui_lifetime" or "first_run_lifetime") guiStarted = true;
                            if (!guiStarted) startup?.CancellationToken.ThrowIfCancellationRequested();
                            startup?.ReportStage(value switch
                            {
                                "compose_foundation" => "加载设置与服务",
                                "compose_shell" => "准备界面",
                                "first_run" => "准备首次设置",
                                "startup_local_instances" => "读取本地版本",
                                "startup_local_recovery" => "恢复本地事务",
                                "startup_sidecar_discovery" => "发现插件接口",
                                "startup_settings_metadata" => "加载设置目录",
                                "startup_initial_policies" => "应用启动偏好",
                                "startup_hidden_native_shell" or "startup_first_run_native_shell" => "准备原生窗口",
                                "startup_controller_projection" or "startup_final_layout" => "准备页面布局",
                                "startup_destination_facts" => "加载初始页面资料",
                                "startup_install_catalog" => "加载安装目录",
                                "startup_resource_catalog" => "加载资源目录",
                                "startup_update_preferences" => "读取更新状态",
                                "startup_update_discovery" => "检查启动器更新",
                                "startup_native_fonts_media_icons" or "startup_first_run_scene" => "准备字体与图像",
                                "startup_ready" or "startup_first_run_ready" => "初始化完成",
                                "gui_lifetime" => "启动完成",
                                _ => "准备启动器"
                            });
                        }, folders, locationLocked, storageStartup.Error, instance, disableHardwareAcceleration, startup)).ConfigureAwait(false);
                    return exitCode;
                }
                catch (Exception exception) when (startup is not null && !guiStarted
                    && exception is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException)
                {
                    log?.Error("Startup", "Initialization failed at " + stage, ExceptionDiagnostics.Describe(exception));
                    try { Console.Error.WriteLine("启动初始化失败：" + LogRedactor.Redact(ExceptionDiagnostics.Describe(exception))); }
                    catch (IOException) { }
                    bool retry = await startup.WaitForRetryAsync("初始化失败，请查看启动日志后重试或关闭。").ConfigureAwait(false);
                    if (!retry) { exitCode = 1; return exitCode; }
                    log?.Dispose(); log = null;
                    if (fileSink is not null) await fileSink.DisposeAsync().ConfigureAwait(false);
                    fileSink = null;
                    stage = "retry_initialization";
                }
            }
        }
        catch (OperationCanceledException) when (startup?.CancellationToken.IsCancellationRequested == true)
        {
            exitCode = 0;
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            string message = $"Launcher session failed stage={stage}";
            string detail = ExceptionDiagnostics.Describe(exception);
            log?.Error("Launcher", message, detail);
            if (log is null && fileSink is not null)
            {
                // Keep early folder/schema failures in the same file, before a host store exists.
                LogEntry bootstrap = new(0, DateTimeOffset.UtcNow, LogLevel.Error, "Launcher",
                    LogRedactor.Redact(message), LogRedactor.Redact(detail));
                fileSink.Write(bootstrap, bootstrap.ToDisplayText());
            }
            // A directory/bootstrap failure may precede the host logger entirely.
            try { Console.Error.WriteLine($"{message}{Environment.NewLine}{detail}"); } catch (IOException) { }
            if (startup is not null && !startup.Completion.IsCompleted)
            {
                startup.ReportFailure("启动失败，请查看启动日志后关闭窗口。");
                await startup.Completion.ConfigureAwait(false);
            }
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            if (startup?.ShellReadyElapsed is { } shellReady)
                log?.Info("Startup", FormattableString.Invariant($"shell_handoff_ms={shellReady.TotalMilliseconds:F2}"));
            log?.Info("Launcher", $"Session ended pid={Environment.ProcessId} exit_code={exitCode} last_stage={stage}");
            log?.Dispose();
            if (fileSink is not null) await fileSink.DisposeAsync().ConfigureAwait(false);
            if (startup is not null) await startup.DisposeAsync().ConfigureAwait(false);
            if (instance is not null) await instance.DisposeAsync().ConfigureAwait(false);
            if (exitCode == 0) _restartAfterExit?.Invoke();
        }
    }

    private static async Task<int> RunAsync(string[] args, Action<LogService> onLogReady, Action<FileLogSink> onSinkReady,
        Action<string> setStage, AppFolders folders, bool locationLocked, Nexa.Xsr.XsrError? storageStartupError,
        DesktopSingleInstance? instance, bool disableHardwareAcceleration, AvaloniaUiStartupSession? startup)
    {
        // Composition root: the two-phase foundation composition. Phase one declares every
        // foundation module's state into one shared builder; phase two builds the store once
        // and constructs the services over it. Trim analysis therefore sees the real
        // foundation call graph, not an empty shell.
        var setup = new FirstRunService(folders.Root, LauncherStorageLocation.LocatorPath, locationLocked, ResolveInformationalVersion());
        bool validateSetup = args.Contains("--validate-setup", StringComparer.OrdinalIgnoreCase);
        // A failed queued move needs the source-root Settings UI so the user can cancel it.
        if (validateSetup || (storageStartupError is null && !args.Contains("--validate-shell", StringComparer.OrdinalIgnoreCase) && setup.Read().Required))
        {
            setStage("first_run");
            return RunFirstRun(args, setup, validateSetup, instance, disableHardwareAcceleration, startup, setStage);
        }
        bool safeMode = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
        string logFilePath = Path.Combine(folders.EnsureFolder(FolderNames.Logs), "launcher.log");
        FileLogSink sink = new(logFilePath);
        onSinkReady(sink);
        setStage("ensure_settings_folder");
        string settingsFolder = folders.EnsureFolder(FolderNames.Settings);
        setStage("ensure_profiles_folder");
        string profilesFolder = folders.EnsureFolder(FolderNames.Profiles);
        string minecraftRootDirectory = new DefaultMinecraftRootProvider().ResolveRoot();
        string javaRuntimeRootDirectory = Path.Combine(minecraftRootDirectory, "runtime");

        setStage("declare_host_state");
        SettingsSchema settingsSchema = LauncherDefaults.CreateSchema();
        // This context exists before the host store is built. Its observer is therefore the
        // production publication path, and PXML later loads into the very same render tree.
        XsrUiRuntimeContext uiRuntime = new();
        DesktopUiIntentSink uiIntents = new();

        // The operation log rides the same store observation as the render bridge: command,
        // query, state, event, scheduler, and lifecycle telemetry flow into LogService through
        // one composition-root wiring, with the logging domain excluded to prevent recursion.
        XsrOperationLog operationLog = new();
        RuntimeTraceSession runtimeTraces = new();
        IXsrDispatchObserver dispatchObservation = RuntimeTraceSession.Combine(operationLog.Dispatch, runtimeTraces);
        XsrCompositeStateObserver stateObservation = new(uiRuntime.StateBridge, RuntimeTraceSession.Combine(operationLog.State, runtimeTraces));
        Nexa.Services.Updates.LauncherBuildIdentity buildInfo = ResolveBuildInfo();
        string channel = buildInfo.Channel;
        bool consoleAttached = Console.IsOutputRedirected
            || (OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero);
        setStage("compose_foundation");
        using FoundationHost host = FoundationComposer.ComposeWithJavaRuntimeRootAndCacheDirectory(
            new LauncherSettingsJsonPort(System.IO.Path.Combine(settingsFolder, "settings.json"), settingsSchema),
            settingsSchema,
            new ProtectedLaunchProfilePort(System.IO.Path.Combine(profilesFolder, "profiles.json")),
            observer: stateObservation,
            declareHostState: LaunchPageState.DeclareState,
            minecraftRootDirectory: minecraftRootDirectory,
            javaRuntimeRootDirectory: javaRuntimeRootDirectory,
            cacheDirectory: Path.Combine(folders.Root, "cache"),
            configureLogging: logging =>
            {
                if (consoleAttached || buildInfo.DiagnosticsRequired) logging.MaximumLevel = LogLevel.RealTime;
                if (consoleAttached) logging.AddSink(new ConsoleLogSink());
                logging.AddSink(sink);
                onLogReady(logging);
                operationLog.Attach(logging);
                logging.Info("Launcher", $"Session started pid={Environment.ProcessId} version={ResolveInformationalVersion()} channel={channel} level={logging.MaximumLevel} runtime={Environment.Version} os={System.Runtime.InteropServices.RuntimeInformation.OSDescription} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                logging.Info("Launcher", "Foundation composition started; loading persisted profiles and settings.");
            });
        if (storageStartupError is not null)
            host.Logging.Warn("Storage", "Startup storage recovery was not completed; retaining the source data root: " + storageStartupError.Message);
        await using var accountServiceLifetime = host.Accounts;
        using var telemetryLifetime = host.Telemetry;
        host.Accounts.ConfigureRegionalPolicy(Nexa.Services.RegionalPolicy.Current);
        DesktopFunctionPatches functionPatches = new();
        DesktopSidecarSignals sidecarSignals = new();
        DesktopSidecarUiPatches sidecarUi = new(host.StateStore);
        await using var sidecars = SidecarStartup.Create(host.Logging, functionPatches.Admission, sidecarSignals.Admission, sidecarUi.Admission, sidecarUi.ModuleAdmission);
        SidecarHostApi sidecarApi = new(sidecars, dispatchObservation);
        await using var sidecarLifetime = SidecarStartup.StartLifetime(sidecars, host.Logging, !safeMode);
        // The session lifecycle narrates startup/shutdown milestones at Info: every subsystem
        // the composition root brings up (and later stops) is a phase on one shared timeline.
        XsrLifecycle session = new("LauncherSession", RuntimeTraceSession.Combine(operationLog.Lifecycle, runtimeTraces));
        session.Enter(XsrLifecyclePhase.Starting);
        host.Logging.Info("Launcher", "Foundation composition completed; registering runtime routes.");
        setStage("compose_runtimes");
        uiIntents.IntentEmitted += (_, e) => operationLog.WriteIntent(e.Intent.Command, e.Intent.CorrelationId);
        bool StorageIdle() => !host.Tasks.ReadEntries().Any(entry => !entry.IsTerminal)
                && !host.StateStore.ReadCollection<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot>(
                    host.StateStore.Resolve(Nexa.Services.Minecraft.Process.MinecraftProcessStateComposition.SessionsKey)).Items
                    .Any(process => process.State is Nexa.Services.Minecraft.Process.MinecraftProcessState.Created or Nexa.Services.Minecraft.Process.MinecraftProcessState.Running)
                && host.StateStore.ReadAppliedValue(host.StateStore.Resolve(Nexa.Services.Minecraft.Launch.MinecraftLaunchProgressState.SnapshotKey))
                    is not Nexa.Services.Minecraft.Launch.MinecraftLaunchProgressSnapshot { Active: true, IsLaunched: false };
        using var storagePreferences = new StoragePreferencesService(folders, LauncherStorageLocation.LocatorPath, locationLocked, StorageIdle, host.Tasks);
        var contentBackups = new ContentBackupService(Path.Combine(folders.Root, "content-backups"), StorageIdle)
        {
            ReadConfiguredKeepCount = () =>
            {
                var policy = host.SettingsPolicy.Read(new());
                if (!policy.IsSuccess) throw new IOException("无法读取备份保留策略。");
                var keepCount = policy.Value!.Values.Single(item => item.Key == "storage.backup-keep-count");
                if (keepCount.ValidationError is not null) throw new IOException("备份保留策略无效。");
                return int.Parse(keepCount.Value.Value!, System.Globalization.CultureInfo.InvariantCulture);
            },
        };
        var legacyMigration = new LegacyMigrationService(StorageIdle);
        await using var diagnosticHistory = new DurableDiagnosticHistorySink(Path.Combine(folders.Root, "diagnostic-history"));
        host.Logging.AddSink(diagnosticHistory);
        await using var backupMaintenance = new ContentBackupMaintenanceSession(contentBackups, host.Logging);
        using var diagnosticAiHttp = host.CreateHttpClient();
        var diagnosticAi = new DiagnosticAiService(diagnosticAiHttp)
        {
            IsEnabled = () =>
            {
                var policy = host.SettingsPolicy.Read(new());
                return policy.IsSuccess && policy.Value!.Values.Single(item => item.Key == "diagnostics.ai.enabled") is { ValidationError: null, Value.Value: "true" };
            },
        };
        var launchDiagnostics = new Nexa.Services.Minecraft.Launch.MinecraftLaunchPlanDiagnostics();
        FoundationRuntime runtime = FoundationRuntimeComposer.ComposeWithStorageAndMedia(host, storagePreferences, AvaloniaUiScreenshotCodec.Crop, dispatchObservation,
            configureRoutes: (commands, queries) =>
            {
                runtimeTraces.Register(queries);
                MinecraftLaunchDiagnosticsRuntime.Register(queries, launchDiagnostics);
                ContentWorkspaceRuntime.Register(commands, queries, contentBackups, legacyMigration, diagnosticHistory, diagnosticAi);
                WorldSnapshotRuntime.Register(commands, queries, host.StateStore, contentBackups, OperatingSystem.IsMacOS() ? AvaloniaUiWorldEditLease.Acquire : null);
            },
            captureWorld: async (path, token) =>
            {
                await contentBackups.CaptureAsync(path, Path.GetFileName(path),
                new HashSet<string>(StringComparer.Ordinal) { "session.lock", ".nexa-world-readonly" }, token).ConfigureAwait(false);
            },
            acquireWorldSessionLock: OperatingSystem.IsMacOS() ? AvaloniaUiWorldEditLease.Acquire : null);
        // Public provider client IDs are embedded at publish time. Passing them here arms both
        // onboarding device flows and the launch identity resolver's refresh capability.
        using AccountOnboardingRuntime accounts = AccountOnboardingRuntimeComposer.ComposeWithAppearance(
            host,
            folders.Root,
            options: ComposeAccountOnboardingOptions(),
            observer: dispatchObservation);
        string jvmHostPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Nexa.Jvm.Host.exe" : "Nexa.Jvm.Host");
        using MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(
            host,
            minecraftRootDirectory,
            identityResolver: accounts.LaunchIdentityResolver,
            observer: dispatchObservation,
            launcherVersion: buildInfo.ProductVersion,
            launchDiagnostics: launchDiagnostics,
            jvmHostExecutable: jvmHostPath,
            gameWindowAppeared: pid =>
            {
                MinecraftWindowIntegration.DetachGameWindows(pid,
                    "Nexa.Minecraft." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    message => host.Logging.Warn("Launch", message));
                var process = host.StateStore.ReadCollection<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot>(
                    host.StateStore.Resolve(Nexa.Services.Minecraft.Process.MinecraftProcessStateComposition.SessionsKey))
                    .Items.FirstOrDefault(item => item.ProcessId == pid && item.State is Nexa.Services.Minecraft.Process.MinecraftProcessState.Created or Nexa.Services.Minecraft.Process.MinecraftProcessState.Running);
                MinecraftWindowIntegration.ApplyGameWindowTitle(pid, process?.WindowTitle, message => host.Logging.Warn("Launch", message));
            });
        if (safeMode && minecraft.Commands.TryResolve(Nexa.Services.Minecraft.Launch.MinecraftSafeLaunchSessionContract.Set, out var safeRoute))
        {
            var safeResult = await minecraft.Commands.Dispatch(safeRoute, new Nexa.Services.Minecraft.Launch.MinecraftSafeLaunchSessionCommand(true)).Completion.ConfigureAwait(false);
            if (!safeResult.IsSuccess) throw new IOException(safeResult.Error?.Message);
        }
        host.Logging.Debug(
            "Launcher",
            $"Runtime composition completed services={runtime.Host.Services.Count} "
            + $"commands={runtime.Commands.Count + minecraft.Commands.Count + accounts.Commands.Count} "
            + $"queries={runtime.Queries.Count + minecraft.Queries.Count}");

        setStage("load_pxml_shell");
        host.Logging.Info("Launcher", "Loading embedded PXML shell and attaching the host state bridge.");
        XsrUiShell shell = PxmlShellComposer.Compose(
            runtime.Host.StateStore,
            uiRuntime,
            new XsrUiShellOptions
            {
                Title = "NexaCL",
                Version = buildInfo.ProductVersion,
            },
            uiIntents);

        using var languageSession = new DesktopLanguageSession(shell, host.StateStore);

        using DesktopFeedbackService feedback = new();
        using DesktopFeedbackPresenter feedbackPresenter = new(
            shell, uiIntents, feedback, runtime.Host.StateStore);
        if (storageStartupError is not null)
            feedback.Warn("存储恢复尚未完成，继续使用原数据位置。可在“存储与迁移”中查看并取消待迁移后重新预览。\n" + storageStartupError.Message);
        using DesktopTaskBubblePresenter taskBubble = new(shell, runtime.Host.StateStore);
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(host, dispatchObservation);
        using TaskCenterController taskCenterPage = new(
            shell, uiIntents, taskCenter.Commands, runtime.Host.StateStore, taskBubble);

        // The launch page is the first product vertical slice: it routes navigation intents to
        // pages inside the shell content host and dispatches the real launch command.
        setStage("attach_product_controllers");
        AvaloniaUiPlatformActions platformActions = new() { LocalizeContentPicker = shell.Renderer.LocalizeText };
        using var appearanceSession = new DesktopAppearanceSession(shell, host.StateStore, platformActions);
        await using var customAppearance = safeMode ? null : new CustomAppearanceSession(runtime.Queries, host.StateStore, shell, platformActions, message => host.Logging.Warn("Appearance", message));
        await using var mediaSession = safeMode ? null : new DesktopMediaSession(runtime.Queries, runtime.Commands, host.StateStore, shell, uiIntents, platformActions, message => host.Logging.Warn("Media", message));
        using var gameWindows = new DesktopGameWindowSession(host.StateStore, action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            platformActions.HideWindow, platformActions.MinimizeWindow, platformActions.RestoreWindow, platformActions.RequestClose,
            message => host.Logging.Warn("Launch", message));
        using var presentationSession = new DesktopPresentationSession(shell, host.StateStore,
            platformActions.SetWindowResizeEnabled, platformActions.SetAnimationFrameRate,
            runtime.Queries, platformActions.PostToWindow);
        platformActions.InputObserved += kind => host.InputUsage.Record(kind switch
        {
            AvaloniaUiInputKind.Keyboard => InputUsageKind.Keyboard,
            AvaloniaUiInputKind.Mouse => InputUsageKind.Mouse,
            AvaloniaUiInputKind.Touch => InputUsageKind.Touch,
            AvaloniaUiInputKind.Controller => InputUsageKind.Controller,
            _ => InputUsageKind.Unknown,
        });
        using MinecraftLibraryRuntime library = MinecraftLibraryRuntimeComposer.Compose(host, minecraftRootDirectory, minecraft.Instances, dispatchObservation);
        using InstallCatalogRuntime installCatalog = InstallCatalogRuntimeComposer.Compose(host, observer: dispatchObservation);
        using MinecraftInstallRuntime installRun = MinecraftInstallRuntimeComposer.Compose(host, observer: dispatchObservation);
        var folderImports = MinecraftFolderImportRuntimeComposer.Compose(host, library.Service, installRun.Service, dispatchObservation);
        using MinecraftDropController dropController = new(platformActions, folderImports, library.Commands, host.StateStore, feedback);
        // A committed install grows the version library immediately: rescan the active root.
        installRun.Service.Installed += root => _ = library.Service.RefreshAsync();
        installRun.Service.Renamed += (root, previous, current) =>
        {
            var remembered = library.Service.RememberRenamedInstance(root, previous, current);
            if (!remembered.IsSuccess) feedback.Warn("版本已改名，但未能保存选择，请在版本列表中重新选择。");
        };
        await using LaunchPageController launchPage = new(
            shell,
            uiIntents,
            minecraft,
            runtime.Commands,
            runtime.Host.StateStore,
            library, feedback, accountCommands: accounts.Commands,
            directoryEffects: new NativeVersionDirectoryEffects(platformActions), installCatalogCommands: installCatalog.Commands, installCatalogQueries: installCatalog.Queries, installRunCommands: installRun.Commands, recoveryQueries: runtime.Queries);
        launchPage.CopyProcessLogText = platformActions.CopyTextAsync;
        using WardrobePageController wardrobePage = new(shell, uiIntents, accounts.Queries!, accounts.Commands, host.StateStore, feedback);
        wardrobePage.ConfigureFilePicker(platformActions.PickSkinFileAsync);
        wardrobePage.ConfigureOpenUrl(platformActions.OpenHttpsUri);
        using WardrobeLibraryPageController wardrobeLibrary = new(shell, uiIntents, accounts.Queries!, accounts.Commands, host.StateStore, feedback);
        wardrobeLibrary.ConfigureOpenUrl(platformActions.OpenHttpsUri);
        wardrobeLibrary.ConfigureBack(() => uiIntents.Emit(XsrSemanticId.Parse("ui.page.back"), wardrobeLibrary.Page, XsrCorrelationId.Create()));
        wardrobePage.ConfigureLibrary(() => launchPage.OpenWardrobeLibraryPage(wardrobeLibrary.Page, wardrobePage.LibraryButton));
        launchPage.WardrobePage = wardrobePage.Page;
        using BedrockInstallPageController bedrockPage = new(shell, uiIntents, host.StateStore, feedback, platformActions.OpenMinecraftStore, platformActions.OpenHttpsUri);
        launchPage.BedrockInstallPage = bedrockPage.Page;
        using AccountFormController accountForm = new(shell, uiIntents, accounts.Commands,
            runtime.Host.StateStore, launchPage.AccountBody, feedback,
            new NativeAccountUiEffects(platformActions), runtime.Host.Logging);
        var recoveryRoots = host.StateStore.Read<MinecraftLibrarySnapshot>(host.StateStore.Resolve(MinecraftLibraryService.StateKey)).Value?.Directories
            .Select(directory => directory.Path).ToArray() ?? [minecraftRootDirectory];
        await using var installRecovery = new DesktopInstallRecoverySession(installRun.Commands, recoveryRoots,
            message => host.Logging.Warn("Install", message), minecraft.Commands, runtime.Commands);
        await new DesktopStartupReadiness(setStage, startup?.CancellationToken ?? default)
            .RunAsync("startup_local_recovery", token => installRecovery.InitialReady.WaitAsync(token)).ConfigureAwait(false);
        launchPage.Attach();
        using SettingsPageController settingsPage = new(shell, uiIntents, runtime.Queries, runtime.Commands, host.StateStore, feedback);
        settingsPage.CopyJavaDiagnosticsTextAsync = platformActions.CopyTextAsync;
        settingsPage.ConfigureJavaManualDownload(minecraft.Queries, minecraft.Commands, platformActions.OpenHttpsUri);
        settingsPage.OpenAboutLink = platformActions.OpenHttpsUri;
        settingsPage.OpenAdvancedSettingsFile = () =>
        {
            try { platformActions.OpenLocalFile(Path.Combine(settingsFolder, "settings.json")); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or ArgumentException or InvalidOperationException or NotSupportedException)
            { feedback.Error("无法打开本地设置文件。请检查文件是否存在及系统编辑器关联。"); }
        };
        settingsPage.ExportDiagnostics = token => ExportDiagnosticsAsync(runtime.Host, platformActions, buildInfo.ProductVersion, token);
        settingsPage.OpenLogDirectory = () => platformActions.OpenDirectory(Path.GetDirectoryName(logFilePath)!);
        settingsPage.ExportLogs = token => ExportLogsAsync(sink, platformActions, token);
        settingsPage.ConfigureStoragePreferences(async token =>
        {
            token.ThrowIfCancellationRequested();
            string? selected = await platformActions.PickStorageDirectoryAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return selected;
        }, platformActions.RequestClose);
        settingsPage.ConfigureContentWorkspace(async token =>
        {
            token.ThrowIfCancellationRequested();
            var path = await platformActions.PickStorageDirectoryAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return path;
        }, token => platformActions.PickWorkspaceJsonAsync(token),
            () => string.Join("\n", host.Logging.GetSnapshot().Select(entry => entry.Message)));
        settingsPage.ConfigureDiagnosticAi(true);
        settingsPage.TelemetryRequired = buildInfo.DiagnosticsRequired;
        await using var networking = new LauncherNetworkRuntime(host, settingsFolder, buildInfo);
        HttpClient updateHttp = networking.Http;
        string updateRid = networking.RuntimeId;
        string updateChannel = buildInfo.UpdateChannel;
        var rollouts = networking.Rollouts;
        var updateService = networking.Updates;
        var updateQueries = networking.Queries;
        var automaticUpdate = new Nexa.Services.Updates.AutomaticUpdateControl(new Nexa.Platform.Updates.AutomaticUpdateHost());
        bool restartAfterUpdate = false;
        settingsPage.ConfigureUpdates(updateQueries, new(buildInfo.ProductVersion, updateRid, updateChannel), platformActions.OpenHttpsUri,
            automaticUpdate, () => { restartAfterUpdate = true; platformActions.RequestClose(); });
        launchPage.SettingsPage = settingsPage.Page;
        using var resourcesRuntime = ResourceCatalogRuntimeComposer.Compose(observer: dispatchObservation, host: host,
            favoritesPath: Path.Combine(settingsFolder, "resources-favorites.json"), installer: installRun.Service);
        settingsPage.ConfigureOnlineContent(resourcesRuntime.Queries, platformActions.OpenHttpsUri, resourcesRuntime.Commands);
        using var resourcesPage = new ResourcesPageController(shell, uiIntents, resourcesRuntime.Queries,
            host.StateStore, platformActions.OpenHttpsUri, functionPatches, sidecarSignals, sidecarUi);
        launchPage.ResourcesPage = resourcesPage.Page;
        resourcesPage.ConfigureDownloads(resourcesRuntime.Commands!, platformActions.PickDownloadDirectoryAsync, feedback);
        resourcesPage.ConfigureInstanceFilter(installCatalog.Queries, () =>
        {
            var selected = ((MinecraftLibrarySnapshot?)host.StateStore.ReadAppliedValue(host.StateStore.Resolve(MinecraftLibraryService.StateKey)))?.SelectedInstance;
            return selected is null ? null : new Nexa.Services.Minecraft.Install.MinecraftInstallEditQuery(
                Path.GetDirectoryName(Path.GetDirectoryName(selected.DirectoryPath))!, selected.Id);
        });
        using SettingsPageController versionSettings = new(shell, uiIntents, runtime.Queries, runtime.Commands, host.StateStore, feedback,
            () => ((MinecraftLibrarySnapshot?)host.StateStore.ReadAppliedValue(host.StateStore.Resolve(MinecraftLibraryService.StateKey)))?.SelectedInstance?.DirectoryPath);
        launchPage.VersionSettingsPage = versionSettings.Page;
        versionSettings.CopyJavaDiagnosticsTextAsync = platformActions.CopyTextAsync;
        versionSettings.ConfigureJavaManualDownload(minecraft.Queries, minecraft.Commands, platformActions.OpenHttpsUri);
        versionSettings.ExportDiagnostics = settingsPage.ExportDiagnostics;
        versionSettings.OpenManagementDirectory = platformActions.OpenDirectory;
        versionSettings.LaunchManagementInstance = launchPage.LaunchInstance;
        versionSettings.ModifyManagementInstance = launchPage.ModifyInstance;
        versionSettings.JoinManagementServer = launchPage.JoinServer;
        versionSettings.ConfigureOnlineContent(resourcesRuntime.Queries, platformActions.OpenHttpsUri, resourcesRuntime.Commands);
        versionSettings.ConfigureExport(platformActions.PickDownloadDirectoryAsync);
        versionSettings.SelectDataPackFileAsync = () => platformActions.PickDataPackFileAsync();
        versionSettings.CopyScreenshotAsync = platformActions.CopyScreenshotAsync;
        versionSettings.ShareScreenshotAsync = platformActions.ShareScreenshotAsync;
        versionSettings.PickRemediationJava = platformActions.PickJavaFileAsync;
        settingsPage.PickRemediationJava = platformActions.PickJavaFileAsync;
        settingsPage.ConfigureSettingsTransfer(token => platformActions.ReadJsonDocumentAsync(1024 * 1024, () => shell.Renderer.LocalizeText("导入设置"), token),
            (document, token) => platformActions.SaveJsonDocumentAsync(document, 1024 * 1024, () => shell.Renderer.LocalizeText("导出设置"), token));
        versionSettings.ConfigureSettingsTransfer(token => platformActions.ReadJsonDocumentAsync(1024 * 1024, () => shell.Renderer.LocalizeText("导入设置"), token),
            (document, token) => platformActions.SaveJsonDocumentAsync(document, 1024 * 1024, () => shell.Renderer.LocalizeText("导出设置"), token));
        versionSettings.ManagementChanged = () =>
        {
            if (library.Commands.TryResolve(MinecraftLibraryRoutes.Refresh, out var refresh))
                _ = library.Commands.Dispatch(refresh, new MinecraftLibraryRefreshCommand());
        };
        // The launch page projects launch-progress cells into overlay display strings, so the
        // composition root adds its observer to the shared store fan-out.
        using IDisposable launchStateSubscription = stateObservation.Subscribe(launchPage.StateObserver);
        host.Logging.Info("Launcher", "Product controllers attached; initial instance scan scheduled.");
        session.Enter(XsrLifecyclePhase.Running);

        Console.WriteLine(
            $"Nexa foundation composed: {runtime.Host.Services.Count} services, "
            + $"{runtime.Commands.Count} command routes, {runtime.Queries.Count} query routes over one host state store; "
            + $"Minecraft routes: {minecraft.Commands.Count} commands/{minecraft.Queries.Count} queries; "
            + $"Sidecar routes: {sidecarApi.Commands.Count} commands/{sidecarApi.Queries.Count} queries; UI style: {shell.Style}.");
        if (args.Any(argument => string.Equals(argument, "--validate-shell", StringComparison.OrdinalIgnoreCase)))
        {
            setStage("validate_shell");
            host.Logging.Info("Launcher", "Headless shell validation started viewport=1280x800");
            XsrUiScene scene = shell.Render(new XsrUiSize(1280, 800));
            Console.WriteLine($"PXML shell validated: {scene.Count} semantic nodes.");
            host.Logging.Info("Launcher", $"Headless shell validation completed nodes={scene.Count}");
            setStage("shutdown");
            session.Enter(XsrLifecyclePhase.Stopping);
            session.Enter(XsrLifecyclePhase.Stopped);
            appearanceSession.Dispose();
            launchPage.Dispose();
            accountForm.Dispose();
            wardrobePage.Dispose();
            bedrockPage.Dispose();
            settingsPage.Dispose();
            versionSettings.Dispose();
            resourcesPage.Dispose();
            taskCenterPage.Dispose();
            dropController.Dispose();
            await sidecarLifetime.DisposeAsync().ConfigureAwait(false);
            await launchPage.DisposeAsync().ConfigureAwait(false);
            await host.Accounts.DisposeAsync().ConfigureAwait(false);
            return 0;
        }

        await using var telemetry = new LauncherTelemetryRuntime(host, rollouts, updateService, ResolveInformationalVersion(),
            () => typeof(Program).Assembly.GetManifestResourceStream("Nexa.Desktop.Assets.api-client.pfx"));
        var telemetrySession = telemetry.Session;
        using IDisposable? telemetrySubscription = telemetrySession is null ? null : stateObservation.Subscribe(telemetrySession);
        operationLog.Diagnostics = telemetrySession;
        rollouts.Start();
        var installExit = new DesktopInstallExitCoordinator(host.StateStore, installRun.Commands, feedback, platformActions.RequestClose, minecraft.Commands);
        using var launchExit = new DesktopLaunchExitCoordinator(() => minecraft.LaunchCoordinator!.HasPendingFinalization,
            minecraft.LaunchCoordinator!.WaitForFinalizationAsync, platformActions.PostToWindow, platformActions.RequestClose, feedback);
        platformActions.CloseRequested = () => installExit.CanClose() && launchExit.CanClose();
        await using var desktopIntegration = new DesktopIntegrationSession(shell, uiIntents, host.StateStore, platformActions, instance, args,
            message => { host.Logging.Warn("Desktop", message); feedback.Warn(message); }, openFile: dropController.OpenFile, registerProtocol: !safeMode);
        using var commandPalette = new DesktopCommandPalette(shell, uiIntents, platformActions,
            route => desktopIntegration.ActivateProtocol("nexacl://" + route.Id));
        settingsPage.LauncherSafeMode = safeMode;
        settingsPage.NavigateAdvancedCommand = route => desktopIntegration.ActivateProtocol("nexacl://" + route.Id);
        settingsPage.OpenAdvancedCommandPalette = commandPalette.Open;
        settingsPage.CopyAdvancedSettingsTextAsync = platformActions.CopyTextAsync;
        await using var systemPreferences = safeMode ? null : new SystemPreferencesSession(runtime.Queries, host.StateStore,
            platformActions, feedback, desktopIntegration.ActivateProtocol, message => host.Logging.Warn("Desktop", message),
            startupNavigate: args.Any(argument => argument.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)
                || DesktopActivation.TryFile(argument, out _)) ? null : destination =>
                {
                    if (!desktopIntegration.HasExplicitActivation)
                        desktopIntegration.ActivateProtocol("nexacl://" + destination.ToString().ToLowerInvariant());
                },
            setLaunchHints: launchPage.SetStartupHintsVisible);
        int exitCode;
        try
        {
            await PrepareNormalStartupAsync(startup, shell, platformActions, launchPage, settingsPage, versionSettings,
                resourcesPage, customAppearance, mediaSession, presentationSession, systemPreferences, setStage, host.Logging,
                desktopIntegration, sidecarLifetime, host.StateStore).ConfigureAwait(false);
            setStage("gui_lifetime");
            host.Logging.Info("Launcher", "Entering Avalonia GUI lifetime.");
            exitCode = AvaloniaUiShellHost.Run(shell, args, platformActions, disableHardwareAcceleration);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            telemetrySession?.Record("app.failure", "failed");
            throw;
        }
        finally
        {
            // Dispose UI objects on the GUI thread before the first asynchronous handoff.
            appearanceSession.Dispose();
            launchPage.Dispose();
            accountForm.Dispose();
            wardrobePage.Dispose();
            bedrockPage.Dispose();
            settingsPage.Dispose();
            versionSettings.Dispose();
            resourcesPage.Dispose();
            taskCenterPage.Dispose();
            dropController.Dispose();
            installRecovery.Dispose();
            await sidecarLifetime.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(launchPage.DisposeAsync().AsTask(), installRecovery.DisposeAsync().AsTask()).ConfigureAwait(false);
            await host.Accounts.DisposeAsync().ConfigureAwait(false);
            await telemetry.DisposeAsync().ConfigureAwait(false);
            await networking.DisposeAsync().ConfigureAwait(false);
        }
        setStage("shutdown");
        host.Logging.Info("Launcher", $"GUI lifetime completed exit_code={exitCode}; releasing session resources.");
        session.Enter(XsrLifecyclePhase.Stopping);
        session.Enter(XsrLifecyclePhase.Stopped);
        if (restartAfterUpdate && exitCode == 0) _restartAfterExit = automaticUpdate.Restart;
        return exitCode;
    }

    private static int RunFirstRun(string[] args, FirstRunService service, bool validate, DesktopSingleInstance? instance,
        bool disableHardwareAcceleration, AvaloniaUiStartupSession? startup, Action<string> setStage)
    {
        XsrUiRuntimeContext context = new();
        XsrStateStoreBuilder builder = new();
        LaunchPageState.DeclareState(builder);
        var store = builder.Build(context.StateBridge);
        DesktopUiIntentSink intents = new();
        var shell = PxmlShellComposer.Compose(store, context, new XsrUiShellOptions
        { Title = "NexaCL", Version = ResolveInformationalVersion() }, intents);
        using var languageSession = new DesktopLanguageSession(shell, store);
        var runtime = FirstRunRuntimeComposer.Compose(service);
        runtime.Queries.TryResolve(FirstRunContract.Status, out var read);
        var status = runtime.Queries.QueryAsync<FirstRunQuery, FirstRunStatus>(read, new()).AsTask().GetAwaiter().GetResult();
        if (!status.IsSuccess) throw new IOException(status.Error?.Message ?? "无法读取初始设置。");
        AvaloniaUiPlatformActions platform = new() { LocalizeContentPicker = shell.Renderer.LocalizeText };
        List<string> nativeFiles = [];
        using IDisposable? nativeFileSubscription = OperatingSystem.IsMacOS() ? DesktopNativeFileActivation.Subscribe(file =>
        {
            if (instance is not null) instance.QueueActivation(DesktopDestination.Activate, file);
            else if (nativeFiles.Count < 16) nativeFiles.Add(file);
        }) : null;
        platform.ProtocolActivated += uri =>
        {
            if (DesktopActivation.TryParse(uri, out DesktopDestination requested))
            {
                if (instance is not null) instance.QueueActivation(requested);
                else _setupDestination = requested;
                platform.RestoreWindow();
            }
        };
        if (instance is not null) instance.Wake = () => platform.PostToWindow(() =>
        { platform.RestoreWindow(); });
        if (instance is not null) platform.WindowClosed += instance.BeginShutdown;
        using var appearanceSession = new DesktopAppearanceSession(shell, store, platform,
            startup?.Appearance.ThemeMode ?? XsrUiThemeMode.System);
        shell.Renderer.ReducedMotion = startup?.Appearance.ReducedMotion ?? false;
        using var controller = new FirstRunController(shell, intents, store, runtime, status.Value!, platform.PickDirectoryAsync, platform.RequestClose);
        if (validate)
        {
            for (int step = 0; step < 4; step++)
            {
                shell.Render(new XsrUiSize(1024, 600));
                if (step < 3) intents.Emit(Nexa.Xsr.XsrSemanticId.Parse("ui.setup.next"), default, Nexa.Xsr.XsrCorrelationId.Create());
            }
            return 0;
        }
        PrepareFirstRunStartupAsync(startup, shell, platform, setStage).GetAwaiter().GetResult();
        setStage("first_run_lifetime");
        int result = AvaloniaUiShellHost.Run(shell, args, platform, disableHardwareAcceleration);
        if (controller.Completed)
        {
            // Avalonia has one application lifetime per process. Reopen after saving the
            // bootstrap locator so every service starts with the selected data root.
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位启动器程序。"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            DesktopDestination nextDestination = _setupDestination;
            if (instance is not null)
            {
                while (instance.TryTakeActivation(out DesktopInstanceActivation requested))
                {
                    if (requested.Destination != DesktopDestination.Activate) nextDestination = requested.Destination;
                    if (requested.File is { } file && !args.Contains(file, StringComparer.Ordinal)) start.ArgumentList.Add(file);
                }
            }
            foreach (string file in nativeFiles.Distinct(Nexa.Core.PathIdentity.Comparer))
                if (!args.Contains(file, Nexa.Core.PathIdentity.Comparer)) start.ArgumentList.Add(file);
            foreach (string argument in args)
                if (nextDestination == DesktopDestination.Activate || !argument.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(argument);
            if (nextDestination != DesktopDestination.Activate) start.ArgumentList.Add("nexacl://" + nextDestination.ToString().ToLowerInvariant());
            _restartAfterExit = () => System.Diagnostics.Process.Start(start)?.Dispose();
        }
        return result;
    }
}
