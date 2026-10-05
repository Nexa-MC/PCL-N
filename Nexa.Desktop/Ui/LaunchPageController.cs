using System.Globalization;
using Nexa.Pxml;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;

using Nexa.Services.Minecraft.Launch;

using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>
/// The product launch page: the first vertical slice attached to the shell's content host,
/// replicating the legacy experimental launch home's information architecture — an account
/// card (profile identity / picker), a version card (版本 header, instance picker row,
/// the big accent launch button), and the community about card. It reads its facts from host
/// state cells, routes one-shot outcomes to the shared feedback service, emits the launch intent,
/// and dispatches the product-level Minecraft start command through the composed runtime routers.
/// Navigation intents route between this page and placeholders for destinations whose slices
/// have not landed yet.
/// </summary>
internal sealed partial class LaunchPageController : IDisposable, IAsyncDisposable
{
    private static readonly XsrSemanticId LaunchRoute = XsrSemanticId.Parse("ui.navigation.launch");
    private static readonly XsrSemanticId InstallRoute = XsrSemanticId.Parse("ui.navigation.download");

    private static readonly XsrSemanticId LaunchPrimaryCommand = XsrSemanticId.Parse("ui.launch.primary");

    private static readonly XsrSemanticId LaunchInstancesCommand = XsrSemanticId.Parse("ui.launch.instances");
    private static readonly XsrSemanticId LaunchSettingsCommand = XsrSemanticId.Parse("ui.launch.settings");
    private static readonly XsrSemanticId LaunchModifyCommand = XsrSemanticId.Parse("ui.launch.modify");
    private static readonly XsrSemanticId PageBackCommand = XsrSemanticId.Parse("ui.page.back");
    private static readonly XsrSemanticId WidgetAboutCommand = XsrSemanticId.Parse("ui.launch.widget.about");
    private static readonly XsrSemanticId WidgetTriviaCommand = XsrSemanticId.Parse("ui.launch.widget.trivia");
    private static readonly XsrSemanticId WidgetEchoCommand = XsrSemanticId.Parse("ui.launch.widget.echo");
    private static readonly XsrSemanticId WidgetHintCommand = XsrSemanticId.Parse("ui.launch.hint.refresh");

    private static readonly XsrSemanticId AccountSelectCommand = XsrSemanticId.Parse("ui.account.select");
    private static readonly XsrSemanticId AccountDeleteCommand = XsrSemanticId.Parse("ui.account.delete");
    private static readonly XsrSemanticId AccountSwitchCommand = XsrSemanticId.Parse("ui.account.switch");
    private static readonly XsrSemanticId AccountWardrobeCommand = XsrSemanticId.Parse("ui.account.wardrobe");
    private static readonly XsrSemanticId AccountDismissCommand = XsrSemanticId.Parse("ui.account.dismiss");
    private static readonly XsrSemanticId LaunchCancelCommand = XsrSemanticId.Parse("ui.launch.cancel");

    private static readonly XsrSemanticId InstallJavaCommand = XsrSemanticId.Parse("ui.install.java");
    private static readonly XsrSemanticId InstallBedrockCommand = XsrSemanticId.Parse("ui.install.bedrock");
    private static readonly XsrSemanticId InstallMinecraftPageCommand = XsrSemanticId.Parse("ui.install.page.minecraft");
    private static readonly XsrSemanticId InstallForgePageCommand = XsrSemanticId.Parse("ui.install.page.forge");
    private static readonly XsrSemanticId InstallCleanroomPageCommand = XsrSemanticId.Parse("ui.install.page.cleanroom");
    private static readonly XsrSemanticId InstallNeoForgePageCommand = XsrSemanticId.Parse("ui.install.page.neoforge");
    private static readonly XsrSemanticId InstallFabricPageCommand = XsrSemanticId.Parse("ui.install.page.fabric");
    private static readonly XsrSemanticId InstallLegacyFabricPageCommand = XsrSemanticId.Parse("ui.install.page.legacy-fabric");
    private static readonly XsrSemanticId InstallFabricApiPageCommand = XsrSemanticId.Parse("ui.install.page.fabric-api");
    private static readonly XsrSemanticId InstallQuiltPageCommand = XsrSemanticId.Parse("ui.install.page.quilt");
    private static readonly XsrSemanticId InstallQslPageCommand = XsrSemanticId.Parse("ui.install.page.qsl");
    private static readonly XsrSemanticId InstallLabyModPageCommand = XsrSemanticId.Parse("ui.install.page.labymod");
    private static readonly XsrSemanticId InstallOptiFinePageCommand = XsrSemanticId.Parse("ui.install.page.optifine");
    private static readonly XsrSemanticId InstallLiteLoaderPageCommand = XsrSemanticId.Parse("ui.install.page.liteloader");
    private static readonly XsrSemanticId InstallStartCommand = XsrSemanticId.Parse("ui.install.start");
    private static readonly XsrSemanticId InstallVersion1211Command = XsrSemanticId.Parse("ui.install.version.1.21.1");
    private static readonly XsrSemanticId InstallVersion1206Command = XsrSemanticId.Parse("ui.install.version.1.20.6");
    private static readonly XsrSemanticId InstallVersion1201Command = XsrSemanticId.Parse("ui.install.version.1.20.1");
    private static readonly XsrSemanticId InstallLoaderFabricCommand = XsrSemanticId.Parse("ui.install.loader.fabric");
    // No visible button emits this any more, but the catalog flow keeps it as the explicit
    // "back to vanilla" selection reset (version picks and tests still drive it).
    private static readonly XsrSemanticId InstallLoaderVanillaCommand = XsrSemanticId.Parse("ui.install.loader.vanilla");
    private static readonly XsrSemanticId InstallLoaderForgeCommand = XsrSemanticId.Parse("ui.install.loader.forge");
    private static readonly XsrSemanticId InstallLoaderNeoForgeCommand = XsrSemanticId.Parse("ui.install.loader.neoforge");
    private static readonly XsrSemanticId InstallLoaderQuiltCommand = XsrSemanticId.Parse("ui.install.loader.quilt");
    private static readonly XsrSemanticId InstallLoaderOptiFineCommand = XsrSemanticId.Parse("ui.install.loader.optifine");
    private static readonly XsrSemanticId InstallLoaderCleanroomCommand = XsrSemanticId.Parse("ui.install.loader.cleanroom");
    private static readonly XsrSemanticId InstallLoaderLiteLoaderCommand = XsrSemanticId.Parse("ui.install.loader.liteloader");
    private static readonly XsrSemanticId InstallLoaderLegacyFabricCommand = XsrSemanticId.Parse("ui.install.loader.legacy-fabric");
    private static readonly XsrSemanticId InstallLoaderLabyModCommand = XsrSemanticId.Parse("ui.install.loader.labymod");
    private static readonly XsrSemanticId InstallFabricApiAddonCommand = XsrSemanticId.Parse("ui.install.addon.fabric-api");
    private static readonly XsrSemanticId InstallQslAddonCommand = XsrSemanticId.Parse("ui.install.addon.qsl");

    private static readonly XsrSemanticId DownloadNavigationId = XsrSemanticId.Parse("navigation.download");

    // The legacy experimental launch-home palette (light theme).
    private static readonly XsrUiColor CardBackground = new(255, 255, 255, 241);
    private static readonly XsrUiColor CardBorder = new(224, 234, 253);
    private static readonly XsrUiColor PickerBackground = new(238, 242, 247);
    private static readonly XsrUiColor PrimaryText = new(52, 61, 74);
    private static readonly XsrUiColor SecondaryText = new(122, 138, 153);
    private static readonly XsrUiColor BadgeBackground = new(224, 234, 253);
    private static readonly XsrUiColor BadgeText = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonBackground = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonHover = new(19, 112, 243);
    private static readonly XsrUiColor ProfileSecondaryText = new(96, 108, 124);
    private static readonly XsrUiColor ProfileSurface = new(244, 246, 250);
    private static readonly XsrUiColor LaunchProgressTrack = new(224, 234, 253);
    private static readonly XsrUiColor LaunchProgressFill = new(11, 91, 203);
    private static readonly XsrUiColor InstallJavaTint = new(229, 239, 255);
    private static readonly XsrUiColor InstallJavaAccent = new(28, 97, 210);
    private static readonly XsrUiColor InstallJavaHover = new(214, 231, 255);
    private static readonly XsrUiColor InstallBedrockTint = new(235, 244, 233);
    private static readonly XsrUiColor InstallBedrockAccent = new(57, 105, 69);
    private static readonly XsrUiColor InstallBedrockHover = new(222, 238, 219);
    private static readonly XsrUiColor InstallSelectedBorder = new(128, 172, 239);

    private static readonly JavaInstallSubpage[] JavaInstallSubpages =
    [
        new("JavaMinecraftPage", "JavaMinecraftTab", InstallMinecraftPageCommand),
        new("JavaForgePage", "JavaForgeTab", InstallForgePageCommand, "Forge"),
        new("JavaCleanroomPage", "JavaCleanroomTab", InstallCleanroomPageCommand, "Cleanroom"),
        new("JavaNeoForgePage", "JavaNeoForgeTab", InstallNeoForgePageCommand, "NeoForge"),
        new("JavaFabricPage", "JavaFabricTab", InstallFabricPageCommand, "Fabric"),
        new("JavaLegacyFabricPage", "JavaLegacyFabricTab", InstallLegacyFabricPageCommand, "Legacy Fabric"),
        new("JavaFabricApiPage", "JavaFabricApiTab", InstallFabricApiPageCommand, RequiresLoader: "Fabric"),
        new("JavaOptiFabricPage", "JavaOptiFabricTab", XsrSemanticId.Parse("ui.install.page.optifabric"), RequiresLoader: "Fabric"),
        new("JavaQuiltPage", "JavaQuiltTab", InstallQuiltPageCommand, "Quilt"),
        new("JavaQslPage", "JavaQslTab", InstallQslPageCommand, RequiresLoader: "Quilt"),
        new("JavaLabyModPage", "JavaLabyModTab", InstallLabyModPageCommand, "LabyMod"),
        new("JavaOptiFinePage", "JavaOptiFineTab", InstallOptiFinePageCommand, "OptiFine"),
        new("JavaLiteLoaderPage", "JavaLiteLoaderTab", InstallLiteLoaderPageCommand, "LiteLoader"),
    ];

    private static readonly string[] JavaInstallLoaderKeys =
    [
        "JavaLoaderForge", "JavaLoaderCleanroom", "JavaLoaderNeoForge", "JavaLoaderFabric",
        "JavaLoaderLegacyFabric", "JavaLoaderQuilt", "JavaLoaderLabyMod", "JavaLoaderOptiFine", "JavaLoaderLiteLoader",
    ];

    private const string NoAccountName = "未选择账户";
    private const string AccountNeedLoginSummary = "请选择或创建一个账户档案后再启动。";

    private static readonly Dictionary<string, string> LaunchStageDisplay = new(StringComparer.Ordinal)
    {
        ["get_java"] = "获取 Java",
        ["login"] = "登录",
        ["complete_files"] = "补全文件",
        ["get_arguments"] = "获取启动参数",
        ["extract_natives"] = "解压 Natives",
        ["pre_launch"] = "预启动处理",
        ["preflight"] = "检查启动条件",
        ["start_process"] = "启动进程",
        ["wait_window"] = "等待游戏窗口",
        ["end"] = "完成",
    };

    private static readonly Dictionary<string, string> LaunchMethodDisplay = new(StringComparer.Ordinal)
    {
        ["offline"] = "离线模式",
        ["microsoft"] = "微软登录",
    };

    private Task _launchRequest = Task.CompletedTask;
    private MinecraftLaunchProgressSnapshot LaunchSnapshot =>
        _store.ReadAppliedValue(_launchProgressId) as MinecraftLaunchProgressSnapshot
        ?? MinecraftLaunchProgressSnapshot.Empty;
    private bool LaunchBusy => !_launchRequest.IsCompleted || LaunchSnapshot is { Active: true, IsLaunched: false };
    private int _pendingCloseLaunching;
    private Guid? _javaAcquisitionDialog;
    private Guid? _preflightDialog;
    private Guid? _preflightAttempt;
    private int _pickingJava;
    private bool _launchingViaKeyboard;
    private XsrUiEntityId _launchingPage;
    private Dictionary<string, XsrUiEntityId> _launchingEntities = [];

    /// <summary>The composition root attaches this observer to the shared store fan-out.</summary>
    public IXsrStateObserver StateObserver { get; }
    private const string ScanningInstances = "正在扫描本地版本…";
    private const string NoInstances = "未找到可启动的游戏版本";
    private const string NoSelectedProfileLabel = "未选择档案";
    private const string DownloadLabel = "下载游戏";
    private const string LaunchLabel = "启动游戏";
    private const string LaunchUnavailableLabel = "暂不支持启动";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly XsrCommandRouter _foundationCommands;
    private readonly XsrCommandRouter? _accountCommands;
    private long _skinRevision = -1;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly XsrCommandRouter _libraryCommands;
    internal VersionSelectionController Versions { get; }
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly XsrUiEntityId _versionListPage;
    private XsrUiEntityId _versionSettingsPage;
    private XsrUiEntityId _wardrobePage;
    private readonly XsrUiEntityId _installPage;
    private readonly XsrUiEntityId _javaInstallPage;
    private XsrUiEntityId _bedrockInstallPage;
    private readonly Dictionary<string, XsrUiEntityId> _javaInstallEntities;
    private int _presentedJavaInstallPage = -1;
    private string _activeJavaInstallPage = "JavaMinecraftPage";
    private readonly Dictionary<string, XsrUiEntityId> _titleEntities = [];
    private int _titleNavigationDepth = 1;
    private readonly Stack<XsrUiEntityId> _returnFocus = [];
    private readonly Dictionary<string, XsrUiEntityId> _pageEntities;
    private readonly Dictionary<int, XsrUiEntityId> _accountRowEntities = [];
    private readonly Dictionary<XsrUiEntityId, int> _accountRowIndexes = [];
    private readonly PxmlHostIr _accountRowTemplate = PxmlCompiler.Compile(
        PxmlParser.Parse(ReadEmbeddedResource("Ui.AccountProfileRow.pxml")));
    private long _accountRosterRevision = -1;
    private int _presentedAccountIndex = -2;
    private bool? _presentedAccountPicker;
    private string? _accountMotionKey;
    private bool _accountKeyboardFocus;
    private int _presentedWidgetIndex = -1;
    private double _indicatorPosition = double.NaN;
    private int _hintIndex = Random.Shared.Next(LaunchWidgetHints.BuiltIn.Count);
    private readonly object _hintGate = new();
    private readonly TimeProvider _timeProvider;
    private ITimer? _hintTimer;
    private bool _hintTimerRunning;
    private readonly object _refreshGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _refreshTask = Task.CompletedTask;
    private Task _shutdownTask = Task.CompletedTask;
    private bool _attached;
    private bool _disposed;

    public LaunchPageController(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        MinecraftRuntime minecraft,
        XsrCommandRouter foundationCommands,
        XsrStateStore store,
        MinecraftLibraryRuntime library,
        DesktopFeedbackService feedback,
        XsrCommandRouter? accountCommands = null,
        TimeProvider? timeProvider = null,
        IVersionDirectoryEffects? directoryEffects = null,
        XsrCommandRouter? installCatalogCommands = null, XsrQueryRouter? installCatalogQueries = null,
        XsrCommandRouter? installRunCommands = null, XsrQueryRouter? recoveryQueries = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(foundationCommands);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(feedback);
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _foundationCommands = foundationCommands;
        _accountCommands = accountCommands;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installCatalogCommands = installCatalogCommands;
        _installCatalogQueries = installCatalogQueries;
        _installRunCommands = installRunCommands;
        _recoveryQueries = recoveryQueries;
        _store = store;
        _launchProgressId = _store.Resolve(MinecraftLaunchProgressState.SnapshotKey);
        _accountProfilesId = _store.Resolve(AccountStateContract.ProfilesKey);
        _accountSelectedId = _store.Resolve(AccountStateContract.SelectedKey);
        _libraryId = _store.Resolve(MinecraftLibraryContract.StateKey);
        _installCatalogId = _store.Resolve(InstallCatalogStateContract.StateKey);
        _projections = new UiProjectionSignal(_store, _intents);

        _feedback = feedback;
        StateObserver = new LaunchingStateObserver(this);
        _libraryCommands = library.Commands;
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
        Versions = new VersionSelectionController(shell, intents, library.Commands, store, feedback, directoryEffects);
        _versionListPage = Versions.Page;
        _versionSettingsPage = LoadVersionSubpage("VersionSettingsPage", "版本设置");
        _wardrobePage = LoadVersionSubpage("AccountWardrobePage", "更衣橱");
        (_installPage, _) = LoadInstallPage();
        (_javaInstallPage, _javaInstallEntities) = LoadJavaInstallPage();
        InitializeInstallCatalog();
        UpdateJavaInstallSubpageVisibility();
        _bedrockInstallPage = LoadBedrockInstallPage();
        (_launchingPage, _launchingEntities) = LoadLaunchingPage();
        _shell.Tree.Walk(_shell.TitleBar, entity =>
        {
            _titleEntities[_shell.Tree.Name(entity)] = entity;
            return true;
        });
    }

    /// <summary>Subscribes to renderer intents and shows the initial launch page.</summary>
    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attached)
        {
            return;
        }

        _attached = true;
        _intents.IntentEmitted += OnIntentEmitted;
        _shell.Renderer.FramePreparing += OnFramePreparing;
        _shell.StyleChanged += OnShellStyleChanged;
        ShowLaunch();
        Publish(LaunchPageState.ProfileNameKey, NoAccountName);
        Publish(LaunchPageState.InstanceSummaryKey, ScanningInstances);
        Publish(LaunchPageState.SelectedInstanceKey, string.Empty);
        Publish(LaunchPageState.ActionLabelKey, DownloadLabel);
        RefreshAccountPresentation();
        if (_store.TryResolve(XsrSemanticId.Parse("UiLaunchWidgetPage"), out XsrStateId widgetPage)
            && _store.ReadAppliedValue(widgetPage) is int savedPage)
            _shell.Renderer.RebasePagerPage(_pageEntities["LaunchWidgetPager"], Math.Clamp(savedPage, 0, 2));
        RefreshWidgetPresentation();
        Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        _hintTimer = _timeProvider.CreateTimer(_ => AdvanceHint(automatic: true), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ = QueueRefresh();
    }

    /// <summary>Completes when the in-flight instance scan has published its facts.</summary>
    public Task WaitUntilIdle()
    {
        lock (_refreshGate)
        {
            return Task.WhenAll(_refreshTask, _installCatalogTask, _installPrefetchTask, _installEditRead ?? Task.CompletedTask);
        }
    }

    internal XsrUiEntityId AccountBody => _pageEntities["AccountBody"];

    /// <summary>
    /// Re-queries the installed instances and re-commits the version card facts. Exposed for
    /// tests so the asynchronous scan can be awaited deterministically.
    /// </summary>
    public Task RefreshInstancesAsync() => QueueRefresh();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Task save = Task.CompletedTask;
        if (_foundationCommands.TryResolve(FoundationRouteIds.SettingsSet, out XsrCommandId saveWidget))
            save = _foundationCommands.Dispatch(saveWidget, new SettingsSetCommand("UiLaunchWidgetPage",
                _shell.Tree.GetComponent<XsrUiPager>(_pageEntities["LaunchWidgetPager"])!.PageIndex.ToString(CultureInfo.InvariantCulture))).Completion;
        lock (_hintGate) { _disposed = true; _hintTimerRunning = false; _hintTimer?.Dispose(); }
        _projections.Dispose();
        if (_attached)
        {
            _intents.IntentEmitted -= OnIntentEmitted;
            _shell.Renderer.FramePreparing -= OnFramePreparing;
            _shell.StyleChanged -= OnShellStyleChanged;
            _attached = false;
        }

        _lifetimeCancellation.Cancel();
        Versions.Dispose();
        _processLogPage?.Dispose();
        foreach (var dock in _processDocks.Values) { _shell.Tree.Destroy(dock.Power); _shell.Tree.Destroy(dock.Logs); }
        _processDocks.Clear();
        if (_javaChoicePage.IsAssigned) _shell.Tree.Destroy(_javaChoicePage);
        DismissAcquisitionDialog();

        _shutdownTask = CompleteShutdownAsync(save);
    }

    private async Task CompleteShutdownAsync(Task save)
    {
        try { await Task.WhenAll(save, WaitUntilIdle(), _launchRequest).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        finally { _lifetimeCancellation.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new(_shutdownTask);
    }

    private Task QueueRefresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_refreshGate)
        {
            _refreshTask = RefreshLibraryAsync();
            return _refreshTask;
        }
    }

    private async Task RefreshLibraryAsync()
    {
        if (!_libraryCommands.TryResolve(MinecraftLibraryRoutes.Refresh, out XsrCommandId id)) return;
        _ = await _libraryCommands.Dispatch(id, new MinecraftLibraryRefreshCommand(), cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        ProjectLibrary();
    }

    private void ProjectLibrary()
    {
        if (_disposed || _store.ReadAppliedValue(_libraryId) is not MinecraftLibrarySnapshot snapshot) return;
        Publish(LaunchPageState.SelectedInstanceKey, snapshot.SelectedInstance?.Id ?? "");
        Publish(LaunchPageState.InstanceSummaryKey, snapshot.SelectedInstance?.Id ?? (snapshot.IsLoading ? ScanningInstances : NoInstances));
        Publish(LaunchPageState.InstanceDirectoryKey, snapshot.RootDirectory);
        UpdateLaunchButton();
    }

    /// <summary>
    /// Publishes primary-action facts only. Downloading does not require an account; launching
    /// requires a selected profile. Safe from an instance worker as well as the render thread.
    /// </summary>
    private void UpdateLaunchButton()
    {
        bool hasProfile = ReadProfiles().Any(profile => profile.Index == SelectedAccountIndex);
        bool hasInstance = !string.IsNullOrWhiteSpace(ReadCell(LaunchPageState.SelectedInstanceKey));
        if (_pageEntities.TryGetValue("VersionName", out var versionName)
            && _shell.Tree.GetComponent<XsrUiText>(versionName) is { } versionText) versionText.Localize = !hasInstance;
        string label;
        bool enabled;
        if (!hasInstance)
        {
            label = DownloadLabel;
            enabled = true;
        }
        else if (!hasProfile)
        {
            label = NoSelectedProfileLabel;
            enabled = false;
        }
        else if (!SelectedProfileCanLaunch())
        {
            // The account capability logs these kinds in successfully, so the action says
            // honestly that they cannot start a game yet. UpdateLaunchButton is a per-frame
            // projection: it must stay side-effect free — a toast here would re-raise the
            // feedback Changed event every frame and spin a render loop forever.
            label = LaunchUnavailableLabel;
            enabled = false;
        }
        else
        {
            label = LaunchLabel;
            enabled = true;
        }

        bool busy = LaunchBusy && (LaunchSnapshot.InstanceId is null || LaunchSnapshot.InstanceId == ReadCell(LaunchPageState.SelectedInstanceKey))
            && (LaunchSnapshot.MinecraftRootDirectory is null || Nexa.Core.PathIdentity.Comparer.Equals(LaunchSnapshot.MinecraftRootDirectory, ReadCell(LaunchPageState.InstanceDirectoryKey)));
        if (busy) { label = "正在启动…"; enabled = false; }
        Publish(LaunchPageState.ActionBusyKey, busy);
        Publish(LaunchPageState.ActionLabelKey, label);
        Publish(LaunchPageState.ActionEnabledKey, enabled);
        Publish(LaunchPageState.InstanceAvailableKey, hasInstance);
    }

    private bool SelectedProfileCanLaunch()
    {
        LaunchProfileView? profile = ReadProfiles().FirstOrDefault(candidate => candidate.Index == SelectedAccountIndex);
        return profile is not { } selected
            || selected.Kind is LaunchProfileKind.Offline or LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin;
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (HandleProcessIntent(e)) return;
        if (HandleInstallCatalogIntent(e)) return;
        XsrSemanticId command = e.Intent.Command;
        if (command == LaunchRoute)
        {
            ShowLaunch();
            PublishProfileFacts();
            _ = QueueRefresh();
        }
        else if (command == InstallRoute)
        {
            ShowInstallRoot();
        }
        else if (command == InstallJavaCommand)
        {
            OpenSubpage(_javaInstallPage, e.Intent.Source);
            RequestInstallCatalog();
            RefreshJavaInstallPresentation();
        }
        else if (command == InstallBedrockCommand)
        {
            OpenSubpage(_bedrockInstallPage, e.Intent.Source);
        }
        else if (TryGetJavaInstallSubpage(command, out JavaInstallSubpage subpage))
        {
            ShowJavaInstallSubpage(subpage.PageKey);
        }
        else if (command == InstallVersion1211Command)
        {
            SelectInstallVersion("1.21.1", "JavaVersion1211");
        }
        else if (command == InstallVersion1206Command)
        {
            SelectInstallVersion("1.20.6", "JavaVersion1206");
        }
        else if (command == InstallVersion1201Command)
        {
            SelectInstallVersion("1.20.1", "JavaVersion1201");
        }
        else if (command == InstallLoaderFabricCommand)
        {
            SelectInstallLoader("Fabric", "JavaLoaderFabric");
        }
        else if (command == InstallLoaderVanillaCommand)
        {
            SelectInstallLoader("原版 Minecraft", string.Empty);
        }
        else if (command == InstallLoaderForgeCommand)
        {
            SelectInstallLoader("Forge", "JavaLoaderForge");
        }
        else if (command == InstallLoaderNeoForgeCommand)
        {
            SelectInstallLoader("NeoForge", "JavaLoaderNeoForge");
        }
        else if (command == InstallLoaderQuiltCommand)
        {
            SelectInstallLoader("Quilt", "JavaLoaderQuilt");
        }
        else if (command == InstallLoaderOptiFineCommand)
        {
            SelectInstallLoader("OptiFine", "JavaLoaderOptiFine");
        }
        else if (command == InstallLoaderCleanroomCommand)
        {
            SelectInstallLoader("Cleanroom", "JavaLoaderCleanroom");
        }
        else if (command == InstallLoaderLiteLoaderCommand)
        {
            SelectInstallLoader("LiteLoader", "JavaLoaderLiteLoader");
        }
        else if (command == InstallLoaderLegacyFabricCommand)
        {
            SelectInstallLoader("Legacy Fabric", "JavaLoaderLegacyFabric");
        }
        else if (command == InstallLoaderLabyModCommand)
        {
            SelectInstallLoader("LabyMod", "JavaLoaderLabyMod");
        }
        else if (command == InstallFabricApiAddonCommand)
        {
            ToggleInstallAddon("Fabric API", "JavaFabricApiSelect");
        }
        else if (command == InstallQslAddonCommand)
        {
            ToggleInstallAddon("QSL", "JavaQslSelect");
        }
        else if (command == InstallStartCommand)
        {
            NotifyInstallUnavailable();
        }
        else if (command == LaunchPrimaryCommand)
        {
            string instanceId = ReadCell(LaunchPageState.SelectedInstanceKey);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                NavigateToDownload();
            }
            else
            {
                _launchRequest = StartLaunchAsync(instanceId, e.Intent.Source);
            }
        }
        else if (command == LaunchCancelCommand)
        {
            _ = CancelLaunchAsync();
        }
        else if (command == LaunchInstancesCommand)
        {
            OpenSubpage(_versionListPage, e.Intent.Source);
        }
        else if (command == LaunchSettingsCommand)
        {
            OpenSubpage(_versionSettingsPage, e.Intent.Source);
        }
        else if (command == LaunchModifyCommand)
        {
            OpenInstallEditor(e.Intent.Source);
        }
        else if (command == AccountWardrobeCommand)
        {
            OpenSubpage(_wardrobePage, e.Intent.Source);
        }
        else if (command.Value == "ui.launch.restore")
        {
            if (LaunchBusy) ShowLaunchingPage(e.Intent.Source);
        }
        else if (command == PageBackCommand)
        {
            if (_shell.Stage.Navigation.Pop())
            {
                UpdateTitleBar();
                if (_returnFocus.TryPop(out XsrUiEntityId focus))
                    _shell.Renderer.Focus(focus, IsKeyboardIntent(e.Intent.Source));
            }
        }
        else if (command == WidgetAboutCommand || command == WidgetTriviaCommand || command == WidgetEchoCommand)
        {
            XsrUiEntityId pager = _pageEntities["LaunchWidgetPager"];
            _ = _shell.Renderer.SelectPagerPage(pager, command == WidgetTriviaCommand ? 1 : command == WidgetEchoCommand ? 2 : 0);
        }
        else if (command == WidgetHintCommand)
        {
            AdvanceHint();
        }
        else if (command == AccountDeleteCommand)
        {
            XsrUiEntityId row = e.Intent.Source;
            while (row.IsAssigned && !_accountRowIndexes.ContainsKey(row)) row = _shell.Tree.Parent(row);
            if (_accountRowIndexes.TryGetValue(row, out int index)) _ = RemoveAccountAsync(index, _accountRosterRevision);
        }
        else if (command == AccountSelectCommand)
        {
            if (_accountRowIndexes.TryGetValue(e.Intent.Source, out int index))
            {
                _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
                _ = SelectAccountAsync(index, _accountRosterRevision);
            }
        }
        else if (command == AccountSwitchCommand)
        {
            _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
            Publish(LaunchPageState.AccountPickerKey, true);
        }
        else if (command == AccountDismissCommand)
        {
            _accountKeyboardFocus = IsKeyboardIntent(e.Intent.Source);
            Publish(LaunchPageState.AccountPickerKey, false);
        }
        else if (IsDestinationCommand(command))
        {
            if (command.Value == "ui.navigation.community" && ResourcesPage.IsAssigned)
            {
                ClearSubpageHistory();
                _shell.Stage.Navigation.Replace(ResourcesPage);
            }
            else ShowPlaceholder(command.Value == "ui.navigation.settings");
        }
    }

    private bool IsDestinationCommand(XsrSemanticId command) =>
        _shell.NavigationItems.Any(item => item.Command == command);

    private void AdvanceHint(bool automatic = false)
    {
        lock (_hintGate)
        {
            if (_disposed || automatic && !_hintTimerRunning) return;
            _hintIndex = (_hintIndex + Random.Shared.Next(1, LaunchWidgetHints.BuiltIn.Count)) % LaunchWidgetHints.BuiltIn.Count;
            Publish(LaunchPageState.WidgetHintKey, LaunchWidgetHints.BuiltIn[_hintIndex]);
        }
    }

    private async Task RemoveAccountAsync(int index, long revision)
    {
        if (!_foundationCommands.TryResolve(FoundationRouteIds.AccountRemoveProfile, out XsrCommandId route)) return;
        XsrResult result = await _foundationCommands.Dispatch(route, new AccountRemoveProfileCommand(index, revision),
            cancellationToken: _lifetimeCancellation.Token).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess) _feedback.Error($"删除档案失败：{result.Error?.Message}");
    }

    private async Task SelectAccountAsync(int index, long revision)
    {
        if (!_foundationCommands.TryResolve(FoundationRouteIds.AccountSelectProfile, out XsrCommandId route))
        {
            _feedback.Error("账户切换命令未注册。");
            return;
        }

        XsrResult result = await _foundationCommands.Dispatch(route,
            new AccountSelectProfileCommand(index, revision), cancellationToken: _lifetimeCancellation.Token)
            .Completion.ConfigureAwait(false);
        if (_disposed) return;
        if (result.IsSuccess) Publish(LaunchPageState.AccountPickerKey, false);
        else _feedback.Error($"切换档案失败：{result.Error?.Message}");
    }

    private void ShowLaunch()
    {
        ClearSubpageHistory();
        if (!_shell.Stage.Navigation.Current.Equals(_launchPage))
        {
            // Destination switches replace the page: the navigator's back stack is reserved
            // for hierarchical drill-in, not for moving between primary destinations.
            _shell.Stage.Navigation.Replace(_launchPage);
        }

    }

    /// <summary>
    /// Shows the first installation decision as a primary destination. Java and Bedrock are
    /// peer product choices here; their detail pages are pushed only after an explicit choice,
    /// so the title-bar back affordance never doubles as primary navigation.
    /// </summary>
    private void ShowInstallRoot()
    {
        ClearSubpageHistory();
        if (!_shell.Stage.Navigation.Current.Equals(_installPage))
        {
            _shell.Stage.Navigation.Replace(_installPage);
        }
    }

    private static bool TryGetJavaInstallSubpage(XsrSemanticId command, out JavaInstallSubpage subpage)
    {
        foreach (JavaInstallSubpage candidate in JavaInstallSubpages)
        {
            if (candidate.Command == command)
            {
                subpage = candidate;
                return true;
            }
        }

        subpage = null!;
        return false;
    }

    /// <summary>
    /// Selects one embedded configuration surface below the installation input. It does not
    /// push a product navigation page: switching Java/loader details is local configuration,
    /// not a new destination in the shell hierarchy.
    /// </summary>
    private void ShowJavaInstallSubpage(string pageKey)
    {
        if (!_javaInstallEntities.TryGetValue("JavaInstallPager", out XsrUiEntityId pager)
            || !_javaInstallEntities.TryGetValue(pageKey, out XsrUiEntityId page)
            || !IsInstallEntityVisible(page))
        {
            return;
        }

        XsrUiEntityId[] pages = VisibleJavaInstallPages(pager);
        int index = Array.IndexOf(pages, page);
        if (index < 0)
        {
            return;
        }

        _activeJavaInstallPage = pageKey;
        RequestInstallCatalog();
        _ = _shell.Renderer.SelectPagerPage(pager, index);
        RefreshJavaInstallPresentation();
    }

    private sealed record JavaInstallSubpage(
        string PageKey,
        string TabKey,
        XsrSemanticId Command,
        string? Loader = null,
        string? RequiresLoader = null);

    private readonly UiProjectionSignal _projections;
    private readonly XsrStateId _launchProgressId;
    private readonly XsrStateId _accountProfilesId;
    private readonly XsrStateId _accountSelectedId;
    private readonly XsrStateId _libraryId;
    private readonly XsrStateId _installCatalogId;
    private XsrUiEntityId _observedPage;
    private Task? _observedEditRead;
    private bool _editReadCompleted;
    private string? _observedInstallDraft;
    private double _observedInstallOffset;
    private XsrUiSize _observedViewport;
    private bool _presentedLaunchBusy;
    internal int ProjectionPasses { get; private set; }
    private void OnFramePreparing(object? sender, EventArgs e)
    {
        bool quiet = _store.TryResolve(Nexa.Services.Scheduling.WorkSchedulingContract.QuietKey, out var quietId)
            && _store.ReadAppliedValue(quietId) is Nexa.Services.Scheduling.WorkQuietSnapshot { IsQuiet: true };
        bool active = !_store.TryResolve(XsrUiShellWindowState.Activity, out var activityId)
            || _store.ReadAppliedValue(activityId) is not XsrUiWindowActivity activity
            || activity is { IsActive: true, IsMinimized: false };
        _shell.Renderer.OptionalMotionSuspended = quiet || !active;
        bool hints = active && !quiet && !LaunchBusy && _shell.Stage.Navigation.Current == _launchPage
            && _shell.Tree.GetComponent<XsrUiPager>(_pageEntities["LaunchWidgetPager"])!.PageIndex == 1;
        lock (_hintGate)
        {
            if (!_disposed && _hintTimerRunning != hints)
            {
                _hintTimerRunning = hints;
                _ = _hintTimer?.Change(hints ? TimeSpan.FromSeconds(3) : Timeout.InfiniteTimeSpan,
                    hints ? TimeSpan.FromSeconds(3) : Timeout.InfiniteTimeSpan);
            }
        }
        bool busyChanged = _presentedLaunchBusy != LaunchBusy;
        if (busyChanged) { _presentedLaunchBusy = LaunchBusy; UpdateLaunchButton(); }
        var currentPage = _shell.Stage.Navigation.Current;
        bool pageChanged = _observedPage != currentPage;
        if (pageChanged)
        {
            if (_observedPage == _javaInstallPage) ResetInstallSelection();
            _observedPage = currentPage;
            UpdateTitleBar();
        }
        RefreshWidgetPresentation();
        bool completed = _installEditRead?.IsCompleted ?? false;
        bool queryChanged = _observedEditRead != _installEditRead || _editReadCompleted != completed;
        _observedEditRead = _installEditRead; _editReadCompleted = completed;
        bool draftChanged = false;
        if (currentPage == _javaInstallPage)
        {
            string draft = _shell.Tree.GetComponent<XsrUiTextInput>(_javaInstallEntities["JavaInstallVersionInput"])!.ReadDraft();
            double offset = _shell.Tree.GetComponent<XsrUiScroll>(_javaInstallEntities[_activeJavaInstallPage])!.OffsetY;
            draftChanged = draft != _observedInstallDraft || offset != _observedInstallOffset
                || _shell.Renderer.Viewport != _observedViewport;
            _observedInstallDraft = draft; _observedInstallOffset = offset; _observedViewport = _shell.Renderer.Viewport;
        }
        bool wake = _projections.Consume() || queryChanged || pageChanged || busyChanged || draftChanged;
        if (Interlocked.Exchange(ref _pendingCloseLaunching, 0) == 1) { CloseLaunchingPage(); wake = true; }
        if (!wake) return;
        ProjectionPasses++;
        ProjectInstallEditor();
        ProjectProcessFeedback();
        Publish(LaunchPageState.LaunchingVisibleKey, LaunchBusy && currentPage != _launchingPage);
        ProjectLibrary();
        RefreshJavaInstallPresentation();
        ProjectInstallCatalog();
        ProjectInstallEditPlan();
        RefreshAccountPresentation();
    }

    private void RefreshWidgetPresentation()
    {
        XsrUiPager pager = _shell.Tree.GetComponent<XsrUiPager>(_pageEntities["LaunchWidgetPager"])!;
        int index = pager.PageIndex;
        if (index != _presentedWidgetIndex)
        {
            _presentedWidgetIndex = index;
            Publish(LaunchPageState.WidgetAboutLabelKey, index == 0 ? "关于 Nexa，当前卡片" : "查看关于 Nexa");
            Publish(LaunchPageState.WidgetTriviaLabelKey, index == 1 ? "你知道吗，当前卡片" : "查看你知道吗");
            Publish(LaunchPageState.WidgetEchoLabelKey, index == 2 ? "回声洞，当前卡片" : "查看回声洞");
        }
        double position = Math.Clamp(pager.Position, 0, 2);
        if (position == _indicatorPosition) return;
        _indicatorPosition = position;
        UpdateWidgetDot("WidgetAboutDot", "WidgetAboutIndicator", Math.Max(0, 1 - position));
        UpdateWidgetDot("WidgetTriviaDot", "WidgetTriviaIndicator", Math.Max(0, 1 - Math.Abs(position - 1)));
        UpdateWidgetDot("WidgetEchoDot", "WidgetEchoIndicator", Math.Max(0, position - 1));
    }

    private void UpdateWidgetDot(string key, string buttonKey, double activation)
    {
        XsrUiEntityId entity = _pageEntities[key];
        _shell.Tree.GetComponent<XsrUiElement>(entity)!.Height = 6 + 10 * activation;
        XsrUiEntityId button = _pageEntities[buttonKey];
        _shell.Tree.GetComponent<XsrUiElement>(button)!.Height = 6 + 10 * activation;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.Background = new XsrUiColor(11, 91, 203,
            (byte)Math.Round(64 + 191 * activation));
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Layout);
    }

    internal XsrUiEntityId VersionSettingsPage
    {
        set { _shell.Tree.Destroy(_versionSettingsPage); _versionSettingsPage = value; }
    }
    internal XsrUiEntityId WardrobePage
    {
        set { _shell.Tree.Destroy(_wardrobePage); _wardrobePage = value; }
    }
    internal XsrUiEntityId BedrockInstallPage
    {
        set { _shell.Tree.Destroy(_bedrockInstallPage); _bedrockInstallPage = value; }
    }
    internal XsrUiEntityId SettingsPage { get; set; }
    internal XsrUiEntityId ResourcesPage { get; set; }


    private int SelectedAccountIndex => _store.ReadAppliedValue(_accountSelectedId) is int index ? index : -1;

    private void Publish<T>(XsrSemanticId key, T value)
    {
        XsrStateId id = _store.Resolve(key);
        if (!Equals(_store.ReadAppliedValue(id), value)) _store.Publish(id, value);
    }

    private string ReadCell(XsrSemanticId key) =>
        _store.Read<string>(_store.Resolve(key)).Value ?? string.Empty;

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadLaunchPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId host = _shell.Tree.Create("launch-page-host");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(
            page,
            entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.Length > 0)
                {
                    entities[key] = entity;
                }

                return true;
            });
        StyleLaunchPage(page, entities);
        return (page, entities);
    }
}
