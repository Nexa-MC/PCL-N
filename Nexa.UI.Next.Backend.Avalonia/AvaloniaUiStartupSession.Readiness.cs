using Avalonia.Threading;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiStartupSession
{
    private XsrUiShell? _preparedShell;
    private AvaloniaUiShellWindow? _preparedWindow;
    private bool _shellWarmed;
    private TaskCompletionSource? _retryRequested;

    public async Task InvokeAsync(Action action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, token);
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, linked.Token);
    }

    public async Task InvokeAsync(Func<Task> action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, token);
        CancellationToken cancellation = linked.Token;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            return action();
        }, DispatcherPriority.Normal).WaitAsync(cancellation).ConfigureAwait(false);
    }

    public Task PrepareShellAsync(XsrUiShell shell, AvaloniaUiPlatformActions? actions = null,
        CancellationToken token = default) => InvokeAsync(() =>
    {
        if (_presented) throw new InvalidOperationException("The product shell is already visible.");
        if (_preparedShell == shell) return;
        _preparedWindow?.DiscardStartup();
        using Stream? icon = AvaloniaUiShellHost.TryOpenProductAsset("Nexa.Desktop.Assets.icon.png");
        _preparedWindow = AvaloniaUiShellLifetime.Prepare(shell, icon);
        _preparedShell = shell;
        _shellWarmed = false;
        actions?.AttachForStartup(_preparedWindow);
        while (_pendingProtocols.TryDequeue(out string? uri)) actions?.OnProtocolActivated(uri);
    }, token);

    public Task WarmUpShellAsync(XsrUiShell shell, CancellationToken token = default) => InvokeAsync(() =>
    {
        if (_preparedShell != shell || _preparedWindow is null)
            throw new InvalidOperationException("The hidden product shell has not been prepared.");
        _preparedWindow.PrepareStartupScene();
        _shellWarmed = true;
    }, token);

    public Task DiscardPreparedShellAsync() => InvokeAsync(DiscardPreparedShell);

    private void DiscardPreparedShell()
    {
        _preparedWindow?.DiscardStartup();
        _preparedWindow = null;
        _preparedShell = null;
        _shellWarmed = false;
    }

    /// <summary>The failed attempt must be disposed before awaiting this native retry choice.</summary>
    public async Task<bool> WaitForRetryAsync(string message, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (_presented || Completion.IsCompleted || CancellationToken.IsCancellationRequested) return false;
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await InvokeAsync(() =>
        {
            DiscardPreparedShell();
            _retryRequested = request;
            Stage = message;
            _window?.SetStage(message, true, canRetry: true);
        }, token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, token);
        try
        {
            await request.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            ReportStage("正在重试启动");
            return true;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { return false; }
        finally { _retryRequested = null; }
    }
}
