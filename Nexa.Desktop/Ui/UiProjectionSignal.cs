using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

/// <summary>Publisher threads only set a wake cell; projections consume it on the render thread.</summary>
internal sealed class UiProjectionSignal : IDisposable
{
    private readonly XsrStateStore _store;
    private readonly DesktopUiIntentSink _intents;
    private int _pending = 1;
    internal UiProjectionSignal(XsrStateStore store, DesktopUiIntentSink intents)
    {
        _store = store; _intents = intents;
        store.Changed += OnChanged;
        intents.IntentEmitted += OnIntent;
    }
    private void OnChanged(XsrStateChange change)
    {
        if (!change.SemanticId.ToString().StartsWith("logging.", StringComparison.Ordinal))
            Interlocked.Exchange(ref _pending, 1);
    }
    private void OnIntent(object? sender, DesktopUiIntentEventArgs args) => Interlocked.Exchange(ref _pending, 1);
    internal bool Consume() => Interlocked.Exchange(ref _pending, 0) != 0;
    public void Dispose() { _store.Changed -= OnChanged; _intents.IntentEmitted -= OnIntent; }
}
