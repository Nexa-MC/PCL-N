using System.Globalization;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed class DesktopLanguageSession : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly XsrStateStore _state;
    private readonly XsrStateId _language;
    private readonly UiLocalizationCatalog _catalog = new();
    private readonly string _systemLanguage;
    private string? _previous;
    private int _pending = 1;
    private readonly Func<string, string>? _previousResolver;

    internal DesktopLanguageSession(XsrUiShell shell, XsrStateStore state, string? systemLanguage = null)
    {
        _shell = shell; _state = state; state.TryResolve(XsrSemanticId.Parse("UiLanguage"), out _language);
        _systemLanguage = systemLanguage ?? CultureInfo.CurrentUICulture.Name;
        _previousResolver = shell.Renderer.TextLocalizer;
        state.Changed += OnChanged;
        shell.Renderer.FramePreparing += OnFrame; OnFrame(this, EventArgs.Empty);
    }
    private void OnChanged(XsrStateChange change) { if (change.Id == _language) Interlocked.Exchange(ref _pending, 1); }
    private void OnFrame(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;
        string requested = _language.IsAssigned ? _state.Read<string>(_language).Value ?? "auto" : "auto";
        if (requested == _previous) return;
        _previous = requested; _catalog.SetLanguage(requested, _systemLanguage);
        _shell.Renderer.TextLocalizer = _catalog.Translate;
    }
    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
        _shell.Renderer.TextLocalizer = _previousResolver;
    }
}
