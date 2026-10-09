namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    internal async Task PrepareVisibleStartupAsync(CancellationToken token)
    {
        if (_shell.Stage.Navigation.Current != Page && _shell.Stage.Navigation.Current != DetailPage) return;
        // Reuse the tasks started by FramePreparing; never dispatch a second provider read.
        for (int pass = 0; pass < 3; pass++)
        {
            OnFrame(this, EventArgs.Empty);
            Task[] pending = new Task?[] { _searching, _reading, _favoritesReading, _contextReading, _instanceReading, _sourcePolicyReading }
                .Concat(_retainedPages.Values.Select(static page => (Task?)page.Renewal).OfType<Task>())
                .Concat(_icons.Select(static item => (Task)item.Read))
                .Concat(_translations.Select(static item => (Task)item.Read))
                .OfType<Task>().Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(true);
        }
        OnFrame(this, EventArgs.Empty);
    }

    internal void SetStartupOffline()
    {
        if (_shell.Stage.Navigation.Current != Page && _shell.Stage.Navigation.Current != DetailPage) return;
        PauseContinuousList(); _listMediaPaused = true; _listFailure = "部分在线信息暂时不可用，可稍后重试。";
        Cancel(); CancelIcons(); CancelTranslations();
        // Keep the resolved placeholder state until navigation or an explicit retry. A final
        // preparation frame must not re-admit the icon requests that just exhausted its budget.
        _iconPage = _shell.Stage.Navigation.Current;
        _contextStop.Cancel();
        _contextReading = null;
        if (_result is not null)
        {
            _result = _result with
            {
                Notice = string.IsNullOrWhiteSpace(_result.Notice)
                ? "部分在线信息暂时不可用，可稍后重试。" : _result.Notice + " · 部分在线信息暂时不可用，可稍后重试。"
            };
            UpdatePagination();
            return;
        }
        ShowFailure(_entities["ResourceList"], "资源来源暂时不可用。请检查网络后重试。",
            RequestNextPage, _listActions);
        UpdatePagination();
    }
}
