using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

/// <summary>Finite host-owned navigation intents projected into a modal UI.Next overlay.</summary>
internal sealed class DesktopCommandPalette : IDisposable
{
    private static readonly XsrSemanticId Execute = XsrSemanticId.Parse("ui.command-palette.execute");
    private static readonly XsrSemanticId Dismiss = XsrSemanticId.Parse("ui.command-palette.dismiss");
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly AvaloniaUiPlatformActions _platform;
    private readonly Action<DesktopCommandRoute> _navigate;
    private readonly Dictionary<XsrUiEntityId, DesktopCommandRoute> _rows = [];
    private readonly Func<bool>? _previousOpen, _previousClose;
    private readonly Func<bool> _openCallback, _closeCallback;
    private XsrUiEntityId _root, _search, _close, _empty, _previousFocus, _page;
    private string? _filterSignature;
    private bool _previousFocusVisible, _disposed;

    internal DesktopCommandPalette(XsrUiShell shell, DesktopUiIntentSink intents,
        AvaloniaUiPlatformActions platform, Action<DesktopCommandRoute> navigate)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _previousOpen = platform.CommandPaletteOpenRequested; _previousClose = platform.CommandPaletteCloseRequested;
        _openCallback = Open; _closeCallback = Close;
        platform.CommandPaletteOpenRequested = _openCallback; platform.CommandPaletteCloseRequested = _closeCallback;
        shell.Renderer.FramePreparing += OnFrame;
        intents.IntentEmitted += OnIntent;
    }

    internal bool IsOpen => _root.IsAssigned;

    internal XsrUiEntityId Find(string name)
    {
        XsrUiEntityId result = default;
        if (IsOpen) _shell.Tree.Walk(_root, entity => { if (_shell.Tree.Name(entity) == name) result = entity; return true; });
        return result;
    }

    internal bool Open()
    {
        if (_disposed) return false;
        if (IsOpen) return true;
        if (_shell.Stage.Overlays.Any(entity => _shell.Tree.GetComponent<XsrUiOverlayLayer>(entity)?.IsModal == true)) return false;
        _page = _shell.Stage.Navigation.Current;
        _previousFocus = _shell.Renderer.Focused;
        _previousFocusVisible = _shell.Tree.IsAlive(_previousFocus)
            && _shell.Tree.GetComponent<XsrUiInput>(_previousFocus)?.IsFocusVisible == true;
        _root = Node(default, "CommandPalette", XsrUiSemanticRole.Dialog, "命令面板");
        _shell.Tree.SetComponent(_root, new XsrUiInput { Clickable = true });
        Style(_root, new(0, 0, 0, 110));
        var card = Node(_root, "CommandPaletteCard", XsrUiSemanticRole.None, null);
        _shell.Tree.SetComponent(card, new XsrUiElement
        {
            Width = 520,
            MaxWidth = 600,
            MaxHeight = 520,
            HorizontalAlignment = XsrUiAlignment.Center,
            VerticalAlignment = XsrUiAlignment.Start,
            Margin = new(16, 64, 16, 16),
            Padding = XsrUiThickness.Uniform(16),
        });
        _shell.Tree.SetComponent(card, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 8 });
        _shell.Tree.SetComponent(card, new XsrUiScroll());
        Style(card, new(255, 255, 255), 12);
        var heading = Node(card, "CommandPaletteTitle", XsrUiSemanticRole.Text, "命令面板", 28);
        _shell.Tree.SetComponent(heading, new XsrUiText("命令面板"));
        _search = Node(card, "CommandPaletteSearch", XsrUiSemanticRole.TextInput, "筛选导航命令", 36);
        _shell.Tree.GetComponent<XsrUiElement>(_search)!.Padding = new(10, 0, 10, 0);
        _shell.Tree.SetComponent(_search, new XsrUiTextInput { Placeholder = "按名称或命令 ID 筛选", MaximumLength = 128 });
        _shell.Tree.SetComponent(_search, new XsrUiInput { Clickable = true, Focusable = true });
        Style(_search, new(244, 247, 251), 6);
        foreach (var route in DesktopCommandLine.Commands)
        {
            var row = Node(card, "CommandPaletteCommand." + route.Id, XsrUiSemanticRole.Button, route.Label, 32);
            _shell.Tree.SetComponent(row, new XsrUiText(route.Label));
            _shell.Tree.SetComponent(row, new XsrUiInput { Clickable = true, Focusable = true });
            _shell.Tree.SetComponent(row, new XsrUiCommandBinding(Execute));
            _shell.Tree.GetComponent<XsrUiElement>(row)!.Padding = new(10, 0, 10, 0);
            Style(row, new(244, 247, 251), 6); _rows[row] = route;
        }
        _empty = Node(card, "CommandPaletteEmpty", XsrUiSemanticRole.Status, "没有匹配的导航命令。", 30);
        _shell.Tree.SetComponent(_empty, new XsrUiText("没有匹配的导航命令。"));
        _close = Node(card, "CommandPaletteClose", XsrUiSemanticRole.Button, "关闭命令面板", 32);
        _shell.Tree.SetComponent(_close, new XsrUiText("关闭命令面板"));
        _shell.Tree.SetComponent(_close, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(_close, new XsrUiCommandBinding(Dismiss));
        Style(_close, new(230, 239, 252), 6);
        _filterSignature = null;
        _shell.Stage.Show(_root, modal: true);
        Filter(); _shell.Renderer.Focus(_search, showIndicator: false);
        return true;
    }

    internal bool Close()
    {
        if (!IsOpen) return false;
        _shell.Stage.Dismiss(_root); _shell.Tree.Destroy(_root);
        _root = _search = _close = _empty = default; _rows.Clear(); _filterSignature = null;
        if (_shell.Tree.IsAlive(_previousFocus)) _shell.Renderer.Focus(_previousFocus, _previousFocusVisible);
        else _shell.Renderer.Focus(default);
        _previousFocus = default;
        return true;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (_disposed || !IsOpen) return;
        if (_page != _shell.Stage.Navigation.Current) { Close(); return; }
        Filter();
    }

    private void Filter()
    {
        string query = _shell.Tree.GetComponent<XsrUiTextInput>(_search)!.ReadDraft().Trim();
        string signature = query + "\n" + string.Join('\n', DesktopCommandLine.Commands.Select(route => _shell.Renderer.LocalizeText(route.Label)));
        if (signature == _filterSignature) return;
        _filterSignature = signature;
        int count = 0;
        foreach (var (entity, route) in _rows)
        {
            bool visible = Matches(route, query);
            _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = visible;
            _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = visible;
            if (visible) count++;
        }
        _shell.Tree.GetComponent<XsrUiElement>(_empty)!.IsVisible = count == 0;
        _shell.Tree.MarkDirty(_root, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private bool Matches(DesktopCommandRoute route, string query) => route.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
        || _shell.Renderer.LocalizeText(route.Label).Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (_disposed || !IsOpen || _page != _shell.Stage.Navigation.Current || !_shell.Tree.IsAlive(args.Intent.Source)) return;
        if (args.Intent.Command == Dismiss && args.Intent.Source == _close) { Close(); return; }
        if (args.Intent.Command != Execute || !_rows.TryGetValue(args.Intent.Source, out var route)
            || !_shell.Tree.GetComponent<XsrUiElement>(args.Intent.Source)!.IsVisible
            || _shell.Tree.GetComponent<XsrUiInput>(args.Intent.Source)?.Enabled != true
            || !Matches(route, _shell.Tree.GetComponent<XsrUiTextInput>(_search)!.ReadDraft().Trim())) return;
        Close(); _navigate(route);
    }

    private XsrUiEntityId Node(XsrUiEntityId parent, string name, XsrUiSemanticRole role, string? label, double? height = null)
    {
        var entity = _shell.Tree.Create(name);
        _shell.Tree.SetComponent(entity, new XsrUiElement { Height = height });
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(role, label));
        if (parent.IsAssigned) _shell.Tree.Attach(entity, parent);
        return entity;
    }

    private void Style(XsrUiEntityId entity, XsrUiColor background, double radius = 0) =>
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        { Background = background, Foreground = new(52, 61, 74), CornerRadius = radius, FontSize = 13, Hover = new(213, 230, 253) });

    public void Dispose()
    {
        if (_disposed) return;
        Close(); _disposed = true;
        _shell.Renderer.FramePreparing -= OnFrame; _intents.IntentEmitted -= OnIntent;
        if (_platform.CommandPaletteOpenRequested == _openCallback) _platform.CommandPaletteOpenRequested = _previousOpen;
        if (_platform.CommandPaletteCloseRequested == _closeCallback) _platform.CommandPaletteCloseRequested = _previousClose;
    }
}
