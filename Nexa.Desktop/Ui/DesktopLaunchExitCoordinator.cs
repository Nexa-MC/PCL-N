namespace Nexa.Desktop.Ui;

/// <summary>Owned launch overlays and post-exit effects finish before the launcher exits.</summary>
internal sealed class DesktopLaunchExitCoordinator : IDisposable
{
    private readonly Func<bool> _pending;
    private readonly Func<CancellationToken, Task> _wait;
    private readonly Action<Action> _dispatch;
    private readonly Action _close;
    private readonly DesktopFeedbackService _feedback;
    private readonly CancellationTokenSource _stop = new();
    private bool _waiting, _disposed;

    internal DesktopLaunchExitCoordinator(Func<bool> pending, Func<CancellationToken, Task> wait,
        Action<Action> dispatch, Action close, DesktopFeedbackService feedback)
    { _pending = pending; _wait = wait; _dispatch = dispatch; _close = close; _feedback = feedback; }

    internal bool CanClose()
    {
        if (_disposed || !_pending()) return true;
        if (!_waiting)
        {
            _waiting = true;
            _feedback.Info("等待游戏退出、临时文件恢复和退出钩子完成后关闭启动器。");
            _ = CompleteAsync();
        }
        return false;
    }

    private async Task CompleteAsync()
    {
        try
        {
            await _wait(_stop.Token).ConfigureAwait(false);
            _dispatch(() => { if (_disposed) return; _waiting = false; _close(); });
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            _dispatch(() => { if (_disposed) return; _waiting = false; _feedback.Error("启动清理未能完成，请检查运行日志后重试退出。"); });
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); _stop.Dispose(); }
}
