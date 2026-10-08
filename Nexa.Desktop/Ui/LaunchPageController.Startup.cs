namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private bool _startupCatalogOffline;
    internal async Task PrepareStartupAsync(CancellationToken token)
    {
        Task scan;
        lock (_refreshGate) scan = _refreshTask;
        await scan.WaitAsync(token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        ProjectLibrary();
        OnFramePreparing(this, EventArgs.Empty);
    }

    internal async Task PrepareVisibleStartupAsync(CancellationToken token)
    {
        if (_shell.Stage.Navigation.Current != _javaInstallPage) return;
        OnFramePreparing(this, EventArgs.Empty);
        await _installCatalogTask.WaitAsync(token).ConfigureAwait(true);
        OnFramePreparing(this, EventArgs.Empty);
    }

    internal void SetStartupOffline()
    {
        if (_shell.Stage.Navigation.Current != _javaInstallPage) return;
        _startupCatalogOffline = true;
        foreach (var status in _catalogStatus.Values)
        {
            _shell.Tree.GetComponent<Nexa.UI.Next.XsrUiText>(status)!.Content = "版本来源暂时不可用，请稍后重试。";
            _shell.Tree.GetComponent<Nexa.UI.Next.XsrUiElement>(status)!.IsVisible = true;
        }
        _shell.Tree.MarkDirty(_javaInstallPage, Nexa.UI.Next.XsrUiDirtyKinds.Paint);
    }
}
