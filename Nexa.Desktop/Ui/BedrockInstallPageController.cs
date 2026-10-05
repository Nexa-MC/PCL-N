using Nexa.Pxml;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Explicit official-product handoff; Microsoft Store owns installation and entitlement.</summary>
internal sealed class BedrockInstallPageController : IDisposable
{
    private static readonly Uri OfficialProductPage = new(
        "https://www.xbox.com/en-US/games/store/minecraft-for-windows/9NBLGGH2JHXJ");
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly DesktopFeedbackService _feedback;
    private readonly Action _openStore;
    private readonly Action<Uri> _openBrowser;
    private readonly bool _supportsStore;
    private readonly Dictionary<string, XsrUiEntityId> _nodes = [];
    private string _status;
    private bool _dirty, _disposed, _dispatching;

    internal BedrockInstallPageController(XsrUiShell shell, DesktopUiIntentSink intents,
        XsrStateStore store, DesktopFeedbackService feedback, Action openStore,
        Action<Uri> openBrowser, bool? supportsStore = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(openStore);
        ArgumentNullException.ThrowIfNull(openBrowser);
        _shell = shell; _intents = intents; _feedback = feedback;
        _openStore = openStore; _openBrowser = openBrowser;
        _supportsStore = supportsStore ?? OperatingSystem.IsWindows();
        _status = _supportsStore ? "打开 Microsoft Store，继续获取或安装游戏。"
            : "当前系统不支持 Minecraft for Windows 安装。可查看官方产品页。";
        using var source = typeof(BedrockInstallPageController).Assembly.GetManifestResourceStream(
            "Nexa.Desktop.Ui.BedrockInstallPage.pxml")
            ?? throw new InvalidOperationException("Missing Bedrock installation page.");
        using var reader = new StreamReader(source);
        var template = PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
        var host = shell.Tree.Create("bedrock-install-loader");
        try
        {
            Page = PxmlUiLoader.Load(template, shell.Tree, store, host);
            shell.Tree.Detach(Page);
        }
        finally { shell.Tree.Destroy(host); }
        shell.Tree.Walk(Page, entity =>
        {
            _nodes[shell.Tree.Name(entity)] = entity;
            bool button = shell.Tree.GetComponent<XsrUiInput>(entity) is not null;
            shell.Tree.SetComponent(entity, new XsrUiVisualStyle
            {
                Foreground = button ? DesktopUiPalette.CapsuleForeground : new(52, 61, 74),
                Background = button ? DesktopUiPalette.CapsuleBackground : XsrUiColor.Transparent,
                Hover = DesktopUiPalette.CapsuleHover,
                FontSize = 14,
                CornerRadius = button ? 10 : 0,
                WrapText = !button,
                TextAlignment = button ? XsrUiTextAlignment.Center : XsrUiTextAlignment.Start,
            });
            return true;
        });
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["BedrockInstallTitle"])!.FontSize = 19;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["BedrockInstallTitle"])!.FontWeight = 600;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["BedrockInstallReturn"])!.FontSize = 12;
        shell.Tree.GetComponent<XsrUiVisualStyle>(_nodes["BedrockInstallReturn"])!.Foreground = new(122, 138, 153);
        Present();
        _intents.IntentEmitted += OnIntent;
        shell.Renderer.FramePreparing += OnFrame;
    }

    internal XsrUiEntityId Page { get; }

    private bool IsCurrent => !_disposed && _shell.Tree.IsAlive(Page)
        && _shell.Stage.Navigation.Current == Page;

    private bool Authorized(XsrUiEntityId source, string key)
    {
        if (source != _nodes[key] || !_shell.Tree.IsAlive(source)
            || _shell.Tree.GetComponent<XsrUiInput>(source) is not { Enabled: true, Clickable: true }) return false;
        for (var node = source; node.IsAssigned && _shell.Tree.IsAlive(node); node = _shell.Tree.Parent(node))
        {
            if (_shell.Tree.GetComponent<XsrUiElement>(node)?.IsVisible == false) return false;
            if (node == Page) return true;
        }
        return false;
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs args)
    {
        if (!IsCurrent || _dispatching) return;
        string command = args.Intent.Command.Value;
        bool store = command == "ui.bedrock.install.store";
        if (store ? !_supportsStore || !Authorized(args.Intent.Source, "BedrockInstallStore")
            : command != "ui.bedrock.install.website" || !Authorized(args.Intent.Source, "BedrockInstallWebsite")) return;
        _dispatching = true;
        try
        {
            // Both effects are synchronous, fixed product handoffs. They report no install progress.
            if (store) _openStore(); else _openBrowser(OfficialProductPage);
            if (!IsCurrent) return;
            _status = store ? "已请求打开 Microsoft Store。请在商店中完成获取或安装。"
                : "已请求打开官方产品页。Minecraft for Windows 安装需要 Windows。";
            _feedback.Info(_status);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            if (!IsCurrent) return;
            _status = store ? "无法打开 Microsoft Store。请重试，或使用官方产品页。"
                : "无法打开官方产品页。请检查默认浏览器后重试。";
            _feedback.Error(_status);
        }
        finally
        {
            _dispatching = false;
            if (!_disposed) _dirty = true;
        }
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (IsCurrent && _dirty) Present();
    }

    private void Present()
    {
        var status = _nodes["BedrockInstallStatus"];
        _shell.Tree.GetComponent<XsrUiText>(status)!.Content = _status;
        _shell.Tree.GetComponent<XsrUiSemantic>(status)!.Label = _status;
        var store = _shell.Tree.GetComponent<XsrUiInput>(_nodes["BedrockInstallStore"])!;
        store.Enabled = store.Clickable = store.Focusable = _supportsStore;
        _shell.Tree.MarkDirty(Page, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        _dirty = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intents.IntentEmitted -= OnIntent;
        _shell.Renderer.FramePreparing -= OnFrame;
        if (_shell.Tree.IsAlive(Page)) _shell.Tree.Destroy(Page);
        _nodes.Clear();
    }
}
