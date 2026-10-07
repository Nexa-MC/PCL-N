using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed class DesktopIntegrationSession : IAsyncDisposable
{
    private readonly XsrStateStore _state;
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly DesktopSingleInstance? _instance;
    private readonly Action<DesktopDestination> _activate;
    private readonly Action<string> _report;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrUiShell _shell;
    private readonly HashSet<XsrStateId> _keys = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _registration;
    private bool _disposed;

    internal DesktopIntegrationSession(XsrUiShell shell, DesktopUiIntentSink intents, XsrStateStore state,
        AvaloniaUiPlatformActions platform, DesktopSingleInstance? instance, string[] args, Action<string> report)
    {
        _state = state; _platform = platform; _instance = instance; _report = report; _intents = intents; _shell = shell;
        shell.Tree.SetComponent(shell.Root, new XsrUiContextMenu([
            new("启动", XsrSemanticId.Parse("ui.navigation.launch")),
            new("安装", XsrSemanticId.Parse("ui.navigation.download")),
            new("资源", XsrSemanticId.Parse("ui.navigation.community")),
            new("设置", XsrSemanticId.Parse("ui.navigation.settings")),
            new("最小化", XsrSemanticId.Parse("ui.desktop.minimize")),
            new("退出", XsrSemanticId.Parse("ui.desktop.exit")),
        ]));
        intents.IntentEmitted += OnIntent;
        _activate = destination =>
        {
            platform.RestoreWindow();
            if (destination == DesktopDestination.Activate) return;
            string command = destination switch
            {
                DesktopDestination.Install => "ui.navigation.download",
                DesktopDestination.Resources => "ui.navigation.community",
                DesktopDestination.Settings => "ui.navigation.settings",
                _ => "ui.navigation.launch",
            };
            intents.Emit(XsrSemanticId.Parse(command), default, XsrCorrelationId.Create());
        };
        platform.OpenSettingsRequested = () => _activate(DesktopDestination.Settings);
        platform.LocalizeDesktopText = shell.Renderer.LocalizeText;
        platform.ProtocolActivated += OnProtocol;
        foreach (string key in new[] { "UiTrayEnabled", "UiCloseToTray", "UiMinimizeToTray" })
            if (state.TryResolve(XsrSemanticId.Parse(key), out XsrStateId id)) _keys.Add(id);
        state.Changed += OnChanged;
        ApplyPolicy();
        if (instance is not null) { platform.WindowClosed += instance.BeginShutdown; instance.Wake = Wake; Wake(); }
        else if (args.FirstOrDefault(value => value.StartsWith("nexacl:", StringComparison.OrdinalIgnoreCase)) is { } uri) OnProtocol(uri);
        _registration = Task.Run(async () =>
        {
            string? error = await DesktopProtocolRegistration.RegisterAsync(_stop.Token).ConfigureAwait(false);
            if (!_stop.IsCancellationRequested && error is not null) report(error);
        });
    }

    private void OnProtocol(string uri)
    {
        if (DesktopActivation.TryParse(uri, out DesktopDestination destination))
            _platform.PostToWindow(() => { if (!_disposed) _activate(destination); });
        else _report("不支持的 nexacl:// 链接。");
    }
    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed || args.Intent.Source != _shell.Root) return;
        if (args.Intent.Command.Value == "ui.desktop.minimize") _platform.MinimizeWindow();
        else if (args.Intent.Command.Value == "ui.desktop.exit") _platform.RequestClose();
    }
    private void Wake() => _platform.PostToWindow(() =>
    {
        if (_disposed || _instance is null) return;
        while (_instance.TryTake(out DesktopDestination destination)) _activate(destination);
    });
    private void OnChanged(XsrStateChange change) { if (_keys.Contains(change.Id)) ApplyPolicy(); }
    private bool Read(string key, bool fallback) => _state.TryResolve(XsrSemanticId.Parse(key), out XsrStateId id)
        ? _state.Read<bool>(id).Value : fallback;
    private void ApplyPolicy() => _platform.SetDesktopPolicy(new(Read("UiTrayEnabled", true), Read("UiCloseToTray", false), Read("UiMinimizeToTray", false)));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _state.Changed -= OnChanged;
        _intents.IntentEmitted -= OnIntent;
        _shell.Tree.SetComponent<XsrUiContextMenu>(_shell.Root, null);
        _platform.ProtocolActivated -= OnProtocol;
        _platform.OpenSettingsRequested = null;
        _platform.LocalizeDesktopText = null;
        if (_instance is not null) { _platform.WindowClosed -= _instance.BeginShutdown; _instance.Wake = null; }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _registration.ConfigureAwait(false);
        _stop.Dispose();
    }
}
