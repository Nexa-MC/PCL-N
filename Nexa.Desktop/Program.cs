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
        LogService? log = null;
        FileLogSink? fileSink = null;
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
            bool locationLocked = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEXA_DATA_DIR")
                ?? Environment.GetEnvironmentVariable("PCL_NEXA_DATA_DIR"));
            stage = "complete_storage_transaction";
            // Recover before opening logs, settings or any other root consumer. Await here,
            // before ApplicationSession starts its STA thread: the GUI bootstrap must not
            // resume on a thread-pool thread after asynchronous file copying.
            var storageStartup = await StoragePreferencesService.CompletePendingMigrationAsync(
                folders.Root, LauncherStorageLocation.LocatorPath, locationLocked).ConfigureAwait(false);
            if (storageStartup.IsSuccess) folders = AppFolders.ResolveDefault();
            else
            {
                try { Console.Error.WriteLine("存储恢复未完成，继续使用原数据位置：" + storageStartup.Error?.Message); }
                catch (IOException) { }
            }
            exitCode = await Nexa.Platform.ApplicationSession.RunAsync(() => RunAsync(args, logging => log = logging,
                sink => fileSink = sink, value => stage = value, folders, locationLocked, storageStartup.Error)).ConfigureAwait(false);
            return exitCode;
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
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            log?.Info("Launcher", $"Session ended pid={Environment.ProcessId} exit_code={exitCode} last_stage={stage}");
            log?.Dispose();
            if (fileSink is not null) await fileSink.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAsync(string[] args, Action<LogService> onLogReady, Action<FileLogSink> onSinkReady,
        Action<string> setStage, AppFolders folders, bool locationLocked, Nexa.Xsr.XsrError? storageStartupError)
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
            return RunFirstRun(args, setup, validateSetup);
        }
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
        XsrCompositeStateObserver stateObservation = new(uiRuntime.StateBridge, operationLog.State);
        Nexa.Services.Updates.LauncherBuildIdentity buildInfo = ResolveBuildInfo();
        string channel = buildInfo.Channel;
        bool consoleAttached = Console.IsOutputRedirected
            || (OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero);
        setStage("compose_foundation");
        using FoundationHost host = FoundationComposer.ComposeWithJavaRuntimeRoot(
            new LauncherSettingsJsonPort(System.IO.Path.Combine(settingsFolder, "settings.json"), settingsSchema),
            settingsSchema,
            new ProtectedLaunchProfilePort(System.IO.Path.Combine(profilesFolder, "profiles.json")),
            observer: stateObservation,
            declareHostState: LaunchPageState.DeclareState,
            minecraftRootDirectory: minecraftRootDirectory,
            javaRuntimeRootDirectory: javaRuntimeRootDirectory,
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
        using var telemetryLifetime = host.Telemetry;
        host.Accounts.ConfigureRegionalPolicy(Nexa.Services.RegionalPolicy.Current);
        DesktopFunctionPatches functionPatches = new();
        DesktopSidecarSignals sidecarSignals = new();
        DesktopSidecarUiPatches sidecarUi = new(host.StateStore);
        await using var sidecars = SidecarStartup.Create(host.Logging, functionPatches.Admission, sidecarSignals.Admission, sidecarUi.Admission, sidecarUi.ModuleAdmission);
        SidecarHostApi sidecarApi = new(sidecars, operationLog.Dispatch);
        await using var sidecarLifetime = SidecarStartup.StartLifetime(sidecars, host.Logging);
        // The session lifecycle narrates startup/shutdown milestones at Info: every subsystem
        // the composition root brings up (and later stops) is a phase on one shared timeline.
        XsrLifecycle session = new("LauncherSession", operationLog.Lifecycle);
        session.Enter(XsrLifecyclePhase.Starting);
        host.Logging.Info("Launcher", "Foundation composition completed; registering runtime routes.");
        setStage("compose_runtimes");
        uiIntents.IntentEmitted += (_, e) => operationLog.WriteIntent(e.Intent.Command, e.Intent.CorrelationId);
        using var storagePreferences = new StoragePreferencesService(folders, LauncherStorageLocation.LocatorPath, locationLocked,
            isIdle: () => !host.Tasks.ReadEntries().Any(entry => !entry.IsTerminal)
                && !host.StateStore.ReadCollection<Nexa.Services.Minecraft.Process.MinecraftProcessSnapshot>(
                    host.StateStore.Resolve(Nexa.Services.Minecraft.Process.MinecraftProcessStateComposition.SessionsKey)).Items
                    .Any(process => process.State is Nexa.Services.Minecraft.Process.MinecraftProcessState.Created or Nexa.Services.Minecraft.Process.MinecraftProcessState.Running)
                && host.StateStore.ReadAppliedValue(host.StateStore.Resolve(Nexa.Services.Minecraft.Launch.MinecraftLaunchProgressState.SnapshotKey))
                    is not Nexa.Services.Minecraft.Launch.MinecraftLaunchProgressSnapshot { Active: true, IsLaunched: false },
            tasks: host.Tasks);
        FoundationRuntime runtime = FoundationRuntimeComposer.ComposeWithStorage(host, storagePreferences, operationLog.Dispatch);
        // Public provider client IDs are embedded at publish time. Passing them here arms both
        // onboarding device flows and the launch identity resolver's refresh capability.
        using AccountOnboardingRuntime accounts = AccountOnboardingRuntimeComposer.Compose(
            host,
            options: ComposeAccountOnboardingOptions(),
            observer: operationLog.Dispatch);
        string jvmHostPath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Nexa.Jvm.Host.exe" : "Nexa.Jvm.Host");
        using MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(
            host,
            minecraftRootDirectory,
            identityResolver: accounts.LaunchIdentityResolver,
            observer: operationLog.Dispatch,
            launcherVersion: buildInfo.ProductVersion,
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
        using TaskCenterRuntime taskCenter = TaskCenterRuntimeComposer.Compose(host, operationLog.Dispatch);
        using TaskCenterController taskCenterPage = new(
            shell, uiIntents, taskCenter.Commands, runtime.Host.StateStore, taskBubble);

        // The launch page is the first product vertical slice: it routes navigation intents to
        // pages inside the shell content host and dispatches the real launch command.
        setStage("attach_product_controllers");
        AvaloniaUiPlatformActions platformActions = new();
        using var appearanceSession = new DesktopAppearanceSession(shell, host.StateStore, platformActions);
        using var gameWindows = new DesktopGameWindowSession(host.StateStore, action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            platformActions.HideWindow, platformActions.MinimizeWindow, platformActions.RestoreWindow, platformActions.RequestClose,
            message => host.Logging.Warn("Launch", message));
        using var presentationSession = new DesktopPresentationSession(shell, host.StateStore,
            platformActions.SetWindowResizeEnabled, platformActions.SetAnimationFrameRate);
        platformActions.InputObserved += kind => host.InputUsage.Record(kind switch
        {
            AvaloniaUiInputKind.Keyboard => InputUsageKind.Keyboard,
            AvaloniaUiInputKind.Mouse => InputUsageKind.Mouse,
            AvaloniaUiInputKind.Touch => InputUsageKind.Touch,
            AvaloniaUiInputKind.Controller => InputUsageKind.Controller,
            _ => InputUsageKind.Unknown,
        });
        using MinecraftLibraryRuntime library = MinecraftLibraryRuntimeComposer.Compose(host, minecraftRootDirectory, minecraft.Instances, operationLog.Dispatch);
        using InstallCatalogRuntime installCatalog = InstallCatalogRuntimeComposer.Compose(host, observer: operationLog.Dispatch);
        using MinecraftInstallRuntime installRun = MinecraftInstallRuntimeComposer.Compose(host, observer: operationLog.Dispatch);
        var folderImports = MinecraftFolderImportRuntimeComposer.Compose(host, library.Service, installRun.Service, operationLog.Dispatch);
        using MinecraftDropController dropController = new(platformActions, folderImports, library.Commands, host.StateStore, feedback);
        // A committed install grows the version library immediately: rescan the active root.
        installRun.Service.Installed += root => _ = library.Service.RefreshAsync();
        installRun.Service.Renamed += (root, previous, current) =>
        {
            var remembered = library.Service.RememberRenamedInstance(root, previous, current);
            if (!remembered.IsSuccess) feedback.Warn("版本已改名，但未能保存选择，请在版本列表中重新选择。");
        };
        using LaunchPageController launchPage = new(
            shell,
            uiIntents,
            minecraft,
            runtime.Commands,
            runtime.Host.StateStore,
            library, feedback, accountCommands: accounts.Commands,
            directoryEffects: new NativeVersionDirectoryEffects(platformActions), installCatalogCommands: installCatalog.Commands, installCatalogQueries: installCatalog.Queries, installRunCommands: installRun.Commands, recoveryQueries: runtime.Queries);
        using AccountFormController accountForm = new(shell, uiIntents, accounts.Commands,
            runtime.Host.StateStore, launchPage.AccountBody, feedback,
            new NativeAccountUiEffects(platformActions), runtime.Host.Logging);
        launchPage.Attach();
        using SettingsPageController settingsPage = new(shell, uiIntents, runtime.Queries, runtime.Commands, host.StateStore, feedback);
        settingsPage.OpenAboutLink = platformActions.OpenHttpsUri;
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
        settingsPage.TelemetryRequired = buildInfo.DiagnosticsRequired;
        using var networking = new LauncherNetworkRuntime(host, settingsFolder, buildInfo);
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
        using var resourcesRuntime = ResourceCatalogRuntimeComposer.Compose(host: host, favoritesPath: Path.Combine(settingsFolder, "resources-favorites.json"), installer: installRun.Service);
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
        versionSettings.OpenManagementDirectory = platformActions.OpenDirectory;
        versionSettings.JoinManagementServer = launchPage.JoinServer;
        versionSettings.ConfigureOnlineContent(resourcesRuntime.Queries, platformActions.OpenHttpsUri, resourcesRuntime.Commands);
        versionSettings.ConfigureExport(platformActions.PickDownloadDirectoryAsync);
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

        using var telemetry = new LauncherTelemetryRuntime(host, rollouts, updateService, ResolveInformationalVersion(),
            () => typeof(Program).Assembly.GetManifestResourceStream("Nexa.Desktop.Assets.api-client.pfx"));
        var telemetrySession = telemetry.Session;
        using IDisposable? telemetrySubscription = telemetrySession is null ? null : stateObservation.Subscribe(telemetrySession);
        operationLog.Diagnostics = telemetrySession;
        rollouts.Start();
        var recoveryRoots = host.StateStore.Read<MinecraftLibrarySnapshot>(host.StateStore.Resolve(MinecraftLibraryService.StateKey)).Value?.Directories
            .Select(directory => directory.Path).ToArray() ?? [minecraftRootDirectory];
        using var installRecovery = new DesktopInstallRecoverySession(installRun.Commands, recoveryRoots,
            message => host.Logging.Warn("Install", message), minecraft.Commands, runtime.Commands);
        var installExit = new DesktopInstallExitCoordinator(host.StateStore, installRun.Commands, feedback, platformActions.RequestClose, minecraft.Commands);
        platformActions.CloseRequested = installExit.CanClose;
        setStage("gui_lifetime");
        host.Logging.Info("Launcher", "Entering Avalonia GUI lifetime.");
        int exitCode;
        try { exitCode = AvaloniaUiShellHost.Run(shell, args, platformActions); }
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
        if (restartAfterUpdate && exitCode == 0) automaticUpdate.Restart();
        return exitCode;
    }

    private static int RunFirstRun(string[] args, FirstRunService service, bool validate)
    {
        XsrUiRuntimeContext context = new();
        XsrStateStoreBuilder builder = new();
        LaunchPageState.DeclareState(builder);
        var store = builder.Build(context.StateBridge);
        DesktopUiIntentSink intents = new();
        var shell = PxmlShellComposer.Compose(store, context, new XsrUiShellOptions { Title = "NexaCL" }, intents);
        using var languageSession = new DesktopLanguageSession(shell, store);
        var runtime = FirstRunRuntimeComposer.Compose(service);
        runtime.Queries.TryResolve(FirstRunContract.Status, out var read);
        var status = runtime.Queries.QueryAsync<FirstRunQuery, FirstRunStatus>(read, new()).AsTask().GetAwaiter().GetResult();
        if (!status.IsSuccess) throw new IOException(status.Error?.Message ?? "无法读取初始设置。");
        AvaloniaUiPlatformActions platform = new();
        using var appearanceSession = new DesktopAppearanceSession(shell, store, platform);
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
        int result = AvaloniaUiShellHost.Run(shell, args, platform);
        if (controller.Completed)
        {
            // Avalonia has one application lifetime per process. Reopen after saving the
            // bootstrap locator so every service starts with the selected data root.
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位启动器程序。"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            foreach (string argument in args) start.ArgumentList.Add(argument);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        return result;
    }
}
