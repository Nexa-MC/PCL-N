using Avalonia.Controls;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    public async Task<IReadOnlyList<string>> LocalizeDesktopCaptionsAsync(IReadOnlyList<string> captions, CancellationToken token = default)
        => await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<string>)captions.Select(text => LocalizeDesktopText?.Invoke(text) ?? text).ToArray(),
            global::Avalonia.Threading.DispatcherPriority.Normal, token);

    /// <summary>Call on the native dispatcher after attachment; the returned session owns all subscriptions.</summary>
    public IAsyncDisposable? CreateNativeMediaControls(Func<AvaloniaUiMediaState> capture, Func<bool> enabled,
        Action<string> control, Action<string> report)
    {
        if (OperatingSystem.IsWindows()) return new AvaloniaUiWindowsMediaSession((_owner as Window)?.TryGetPlatformHandle()?.Handle ?? 0,
            capture, enabled, control, report, LocalizeDesktopText?.Invoke("NexaCL 背景音乐") ?? "NexaCL 背景音乐");
        if (OperatingSystem.IsMacOS())
        {
            var media = new AvaloniaUiMacMediaSession(capture, enabled, control, report,
                LocalizeDesktopText?.Invoke("NexaCL 背景音乐") ?? "NexaCL 背景音乐");
            WindowClosed += media.CloseNativeOnUiThread;
            return new MediaCleanupLease(this, media);
        }
        return null;
    }
    private sealed class MediaCleanupLease(AvaloniaUiPlatformActions owner, AvaloniaUiMacMediaSession media) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        { owner.WindowClosed -= media.CloseNativeOnUiThread; await media.DisposeAsync().ConfigureAwait(false); }
    }
}
