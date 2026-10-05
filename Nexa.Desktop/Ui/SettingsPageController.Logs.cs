using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal Action? OpenLogDirectory { get; set; }
    internal Func<CancellationToken, Task<bool>>? ExportLogs { get; set; }
    private CancellationTokenSource? _logExportStop;
    private int _logExportWriting;
    private bool _logExportUiBusy;

    private void BuildDiskLogActions()
    {
        _logExportUiBusy = Volatile.Read(ref _logExportWriting) != 0;
        if (OpenLogDirectory is null && ExportLogs is null) return;
        var card = SettingsCard("SettingsDiskLogActions", new(20, 18, 20, 18), spacing: 10);
        Text(card, "磁盘日志", 18, Ink, height: 28, weight: 600);
        var description = Text(card, "导出当前与保留的历史磁盘日志的时间、等级和模块。日志正文、异常、账户与路径均不会进入压缩包。", 13, Muted, height: 48);
        _shell.Tree.GetComponent<XsrUiVisualStyle>(description)!.WrapText = true;
        var actions = Stack(card, "SettingsDiskLogButtons", XsrUiOrientation.Horizontal, 12);
        if (OpenLogDirectory is not null)
            ManagementButton(actions, "打开日志目录", OpenDiskLogDirectory, 136);
        if (ExportLogs is not null)
        {
            var export = ActionButton(actions, "Management.导出日志", "导出日志", ManagementAction, 112);
            _managementActions[export] = () => WakeOnPlatformCompletion(ExportDiskLogsAsync());
            _shell.Tree.GetComponent<XsrUiInput>(export)!.Enabled = !_logExportUiBusy;
            if (_logExportUiBusy)
                ManagementButton(actions, "取消日志导出", CancelDiskLogExport, 136);
        }
    }

    private void UpdateDiskLogActions()
    {
        if (_instanceDirectory is null && _selected == "privacy" && _logExportUiBusy != (Volatile.Read(ref _logExportWriting) != 0))
            BuildSections();
    }

    private void OpenDiskLogDirectory()
    {
        if (_disposed) return;
        try { OpenLogDirectory?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { _feedback.Error("无法打开日志目录，请检查目录是否存在。"); }
    }

    private async Task ExportDiskLogsAsync()
    {
        if (_disposed || ExportLogs is not { } export || Interlocked.CompareExchange(ref _logExportWriting, 1, 0) != 0) return;
        var stop = new CancellationTokenSource();
        Volatile.Write(ref _logExportStop, stop);
        try
        {
            if (_disposed) { stop.Cancel(); return; }
            bool saved = await export(stop.Token).ConfigureAwait(false);
            if (!_disposed && !stop.IsCancellationRequested && saved) _feedback.Info("日志事实已导出到所选目录。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { if (!_disposed && !stop.IsCancellationRequested) _feedback.Error("日志未导出，请检查日志和保存目录是否可访问。"); }
        finally
        {
            Interlocked.CompareExchange(ref _logExportStop, null, stop);
            stop.Dispose(); Volatile.Write(ref _logExportWriting, 0);
        }
    }

    private void CancelDiskLogExport()
    {
        try { Volatile.Read(ref _logExportStop)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
