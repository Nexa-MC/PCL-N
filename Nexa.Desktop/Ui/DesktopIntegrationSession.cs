using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed class DesktopIntegrationSession : IAsyncDisposable
{
    private static readonly XsrUiContextMenuItem[] DesktopMenuItems =
    [
        new("启动", XsrSemanticId.Parse("ui.navigation.launch")),
        new("安装", XsrSemanticId.Parse("ui.navigation.download")),
        new("资源", XsrSemanticId.Parse("ui.navigation.community")),
        new("设置", XsrSemanticId.Parse("ui.navigation.settings")),
        new("最小化", XsrSemanticId.Parse("ui.desktop.minimize")),
        new("退出", XsrSemanticId.Parse("ui.desktop.exit")),
    ];
    private readonly XsrStateStore _state;
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly DesktopSingleInstance? _instance;
    private readonly Action<DesktopDestination> _activate;
    private readonly Action<string> _report;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrUiShell _shell;
    private readonly Action<string>? _openFile;
    private readonly Queue<string> _pendingFiles = new(16);
    private readonly HashSet<XsrStateId> _keys = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _registration;
    private readonly IDisposable? _nativeFiles;
    private bool _disposed;
    internal bool HasExplicitActivation { get; private set; }
    internal Task InitialReady => _registration;

    internal DesktopIntegrationSession(XsrUiShell shell, DesktopUiIntentSink intents, XsrStateStore state,
        AvaloniaUiPlatformActions platform, DesktopSingleInstance? instance, string[] args, Action<string> report,
        Action<string>? openFile = null, bool registerProtocol = true)
    {
        _state = state; _platform = platform; _instance = instance; _report = report; _intents = intents; _shell = shell;
        _openFile = openFile;
        shell.Renderer.FramePreparing += OnFrame;
        var existingMenu = shell.Tree.GetComponent<XsrUiContextMenu>(shell.Root)?.Items ?? [];
        shell.Tree.SetComponent(shell.Root, new XsrUiContextMenu(DesktopMenuItems.Concat(existingMenu)
            .DistinctBy(item => item.Command)));
        intents.IntentEmitted += OnIntent;
        _activate = destination =>
        {
            platform.RestoreWindow();
            if (destination == DesktopDestination.Activate) return;
            HasExplicitActivation = true;
            string command = destination switch
            {
                DesktopDestination.Install => "ui.navigation.download",
                DesktopDestination.Resources => "ui.navigation.community",
                DesktopDestination.Settings or DesktopDestination.Java or DesktopDestination.Storage or DesktopDestination.About => "ui.navigation.settings",
                DesktopDestination.Tasks => "ui.tasks.open",
                _ => "ui.navigation.launch",
            };
            intents.Emit(XsrSemanticId.Parse(command), default, XsrCorrelationId.Create());
            if (destination is DesktopDestination.Java or DesktopDestination.Storage or DesktopDestination.About)
            {
                string page = destination.ToString().ToLowerInvariant();
                shell.Tree.Walk(shell.Root, section =>
                {
                    if (shell.Tree.Name(section) == "SettingsNav." + page)
                        intents.Emit(XsrSemanticId.Parse("ui.settings.section"), section, XsrCorrelationId.Create());
                    return true;
                });
            }
        };
        platform.OpenSettingsRequested = () => _activate(DesktopDestination.Settings);
        platform.LocalizeDesktopText = shell.Renderer.LocalizeText;
        platform.ProtocolActivated += OnProtocol;
        foreach (string key in new[] { "UiTrayEnabled", "UiCloseToTray", "UiMinimizeToTray" })
            if (state.TryResolve(XsrSemanticId.Parse(key), out XsrStateId id)) _keys.Add(id);
        state.Changed += OnChanged;
        ApplyPolicy();
        if (instance is not null) { platform.WindowClosed += instance.BeginShutdown; instance.Wake = Wake; Wake(); }
        else
        {
            if (args.FirstOrDefault(value => value.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)) is { } uri) OnProtocol(uri);
            foreach (string argument in args)
                if (DesktopActivation.TryFile(argument, out string file)) ScheduleFile(file);
        }
        if (OperatingSystem.IsMacOS()) _nativeFiles = DesktopNativeFileActivation.Subscribe(ScheduleFile, report);
        _registration = registerProtocol ? Task.Run(async () =>
        {
            string? error = await DesktopProtocolRegistration.RegisterAsync(_stop.Token).ConfigureAwait(false);
            if (!_stop.IsCancellationRequested && error is not null) report(error);
        }) : Task.CompletedTask;
    }

    private void OnProtocol(string uri)
    {
        if (DesktopActivation.TryParse(uri, out DesktopDestination destination))
            _platform.PostToWindow(() => { if (!_disposed) _activate(destination); });
        else _report("不支持的 nexacl:// 链接。");
    }
    internal void ActivateProtocol(string uri) => OnProtocol(uri);
    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed) return;
        if (args.Intent.Command.Value.StartsWith("ui.navigation.", StringComparison.Ordinal)
            || args.Intent.Command.Value == "ui.tasks.open") HasExplicitActivation = true;
        if (args.Intent.Source != _shell.Root) return;
        if (args.Intent.Command.Value == "ui.desktop.minimize") _platform.MinimizeWindow();
        else if (args.Intent.Command.Value == "ui.desktop.exit") _platform.RequestClose();
    }
    private void Wake() => _platform.PostToWindow(() =>
    {
        if (_disposed || _instance is null) return;
        while (_instance.TryTakeActivation(out DesktopInstanceActivation activation))
        {
            _activate(activation.Destination);
            if (activation.File is { } file) ScheduleFile(file);
        }
    });
    private void ScheduleFile(string file)
    {
        if (_disposed) return;
        HasExplicitActivation = true;
        if (_pendingFiles.Count >= 16) { _report("待处理整合包过多，请完成当前确认后重试。"); return; }
        _pendingFiles.Enqueue(file);
        _shell.Tree.MarkDirty(_shell.Root, XsrUiDirtyKinds.Paint);
    }
    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed || _pendingFiles.Count == 0) return;
        _platform.RestoreWindow();
        if (_openFile is null) { _report("当前界面尚不能接收整合包文件。"); _pendingFiles.Clear(); return; }
        while (_pendingFiles.TryDequeue(out string? file)) _openFile(file);
    }
    private void OnChanged(XsrStateChange change) { if (_keys.Contains(change.Id)) ApplyPolicy(); }
    private bool Read(string key, bool fallback) => _state.TryResolve(XsrSemanticId.Parse(key), out XsrStateId id)
        ? _state.Read<bool>(id).Value : fallback;
    private void ApplyPolicy() => _platform.SetDesktopPolicy(new(Read("UiTrayEnabled", true), Read("UiCloseToTray", false), Read("UiMinimizeToTray", false)));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _nativeFiles?.Dispose();
        _state.Changed -= OnChanged;
        _intents.IntentEmitted -= OnIntent;
        _shell.Renderer.FramePreparing -= OnFrame;
        _pendingFiles.Clear();
        var remainingMenu = _shell.Tree.GetComponent<XsrUiContextMenu>(_shell.Root)?.Items
            .Where(item => !DesktopMenuItems.Any(owned => owned.Command == item.Command)).ToArray() ?? [];
        _shell.Tree.SetComponent<XsrUiContextMenu>(_shell.Root,
            remainingMenu.Length == 0 ? null : new XsrUiContextMenu(remainingMenu));
        _platform.ProtocolActivated -= OnProtocol;
        _platform.OpenSettingsRequested = null;
        _platform.LocalizeDesktopText = null;
        if (_instance is not null) { _platform.WindowClosed -= _instance.BeginShutdown; _instance.Wake = null; }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _registration.ConfigureAwait(false);
        _stop.Dispose();
    }
}
