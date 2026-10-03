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
    private readonly CultureInfo _systemCulture;
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo? _previousDefaultCulture;
    private readonly string _formatPreference;
    private string? _previous;
    private int _pending = 1;
    private readonly Func<string, string>? _previousResolver;

    internal DesktopLanguageSession(XsrUiShell shell, XsrStateStore state, string? systemLanguage = null, CultureInfo? systemCulture = null)
    {
        _shell = shell; _state = state; state.TryResolve(XsrSemanticId.Parse("UiLanguage"), out _language);
        _systemLanguage = systemLanguage ?? CultureInfo.CurrentUICulture.Name;
        _previousCulture = CultureInfo.CurrentCulture;
        _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        _systemCulture = systemCulture ?? _previousCulture;
        _formatPreference = state.TryResolve(XsrSemanticId.Parse("UiFormatCulture"), out var format)
            ? state.Read<string>(format).Value ?? "auto" : "auto";
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
        CultureInfo culture = _systemCulture;
        if (_formatPreference is "follow-language" or "ui-language")
            culture = CultureInfo.GetCultureInfo(_catalog.Language switch { "zh-Hans" => "zh-CN", "zh-Hant" => "zh-TW", _ => "en-US" });
        else if (_formatPreference != "auto")
        {
            try { culture = CultureInfo.GetCultureInfo(_formatPreference); }
            catch (CultureNotFoundException) { /* Legacy invalid preferences retain system formatting. */ }
        }
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        _shell.Renderer.TextLocalizer = _catalog.Translate;
    }
    public void Dispose()
    {
        _state.Changed -= OnChanged;
        _shell.Renderer.FramePreparing -= OnFrame;
        _shell.Renderer.TextLocalizer = _previousResolver;
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
    }
}
