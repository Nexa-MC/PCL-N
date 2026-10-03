using Nexa.Services.Updates;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId CheckUpdate = XsrSemanticId.Parse("ui.settings.update.check");
    private static readonly XsrSemanticId DownloadUpdate = XsrSemanticId.Parse("ui.settings.update.download");
    private static readonly XsrSemanticId PortableUpdate = XsrSemanticId.Parse("ui.settings.update.portable");
    private static readonly XsrSemanticId UpdateNotes = XsrSemanticId.Parse("ui.settings.update.notes");
    private static readonly XsrSemanticId InstallUpdate = XsrSemanticId.Parse("ui.settings.update.install");
    private static readonly XsrSemanticId RestartUpdate = XsrSemanticId.Parse("ui.settings.update.restart");
    private static readonly XsrSemanticId RollbackUpdate = XsrSemanticId.Parse("ui.settings.update.rollback");
    private IAutomaticUpdateControl? _automaticUpdates;
    private Task<AutomaticUpdateStatus>? _automaticReading, _automaticInstalling;
    private AutomaticUpdateStatus? _automaticStatus;
    private Action? _restartForUpdate;
    private Task? _automaticRefreshDelay;
    private bool _automaticRecoveryAttempted;
    private bool _automaticDiscardReading;
    private bool UpdateNeedsRestart => _automaticStatus?.Phase is "complete" or "rolledback"
        && _automaticStatus.Version != _updateQuery?.CurrentVersion;
    private bool AutomaticUpdateInProgress => _automaticInstalling is not null
        || _automaticStatus?.Phase is "verifying" or "downloading" or "preparing" or "activating";
    private XsrQueryRouter? _updateQueries;
    private NexaUpdateQuery? _updateQuery;
    private Action<Uri>? _openUpdateLink;
    private Task<XsrResult<NexaUpdateStatus>>? _updateReading;
    private NexaUpdateOffer? _updateOffer;
    private string _updateStatus = "通过 Cloudflare 检查当前平台的新版本。";
    private readonly CancellationTokenSource _updateStop = new();

    internal void ConfigureUpdates(XsrQueryRouter queries, NexaUpdateQuery query, Action<Uri> open,
        IAutomaticUpdateControl? automatic = null, Action? restart = null)
    {
        _updateQueries = queries; _updateQuery = query; _openUpdateLink = open;
        _automaticUpdates = automatic; _restartForUpdate = restart;
        if (automatic is not null) { _automaticReading = automatic.ReadAsync(_updateStop.Token); WakeOnPlatformCompletion(_automaticReading); }
    }

    private static bool IsUpdateIntent(XsrSemanticId id) => id == CheckUpdate || id == DownloadUpdate || id == PortableUpdate || id == UpdateNotes
        || id == InstallUpdate || id == RestartUpdate || id == RollbackUpdate;
    private void HandleUpdateIntent(XsrSemanticId id)
    {
        if (id == RestartUpdate && UpdateNeedsRestart) { _restartForUpdate?.Invoke(); return; }
        if (id == InstallUpdate && !AutomaticUpdateInProgress && _automaticStatus?.CanInstall == true && _automaticUpdates is not null && _updateQuery is not null)
        {
            string? version = _updateOffer?.Version ?? _automaticStatus.Version;
            string? channel = _updateOffer is null ? _automaticStatus.Channel : _updateQuery.Channel;
            if (version is null || channel is not ("alpha" or "beta" or "stable")) return;
            _automaticRecoveryAttempted = true;
            _automaticInstalling = _automaticUpdates.InstallAsync(version, channel, _updateStop.Token);
            WakeOnPlatformCompletion(_automaticInstalling); _updateStatus = "正在更新…"; BuildSections(); return;
        }
        if (id == RollbackUpdate && !AutomaticUpdateInProgress && _automaticStatus?.CanRollback == true && _automaticUpdates is not null)
        { _automaticRecoveryAttempted = true; _automaticInstalling = _automaticUpdates.RollbackAsync(_updateStop.Token); WakeOnPlatformCompletion(_automaticInstalling); return; }
        if (id == CheckUpdate && _updateReading is null && _updateQuery is not null && _updateQueries?.TryResolve(NexaUpdateContract.Check, out var route) == true)
        {
            _updateStatus = "正在检查更新…";
            _updateReading = _updateQueries.QueryAsync<NexaUpdateQuery, NexaUpdateStatus>(route, _updateQuery, cancellationToken: _updateStop.Token).AsTask();
            WakeOnPlatformCompletion(_updateReading);
            BuildSections();
        }
        else if (_updateOffer is { } offer && id != CheckUpdate)
        {
            string url = id == DownloadUpdate ? offer.InstallerUrl : id == PortableUpdate ? offer.PortableUrl : offer.ReleaseUrl;
            try { _openUpdateLink?.Invoke(new Uri(url)); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { _feedback.Error("无法打开下载页面，请稍后重试。"); }
        }
    }

    private void UpdateReleaseCheck()
    {
        UpdateAutomaticState();
        if (_updateReading is not { IsCompleted: true } task) return;
        _updateReading = null;
        _updateOffer = PendingQuery.Succeeded(task) ? task.Result.Value!.Offer : null;
        _updateStatus = !PendingQuery.Succeeded(task) ? "暂时无法检查更新，请重试。"
            : _updateOffer is null ? "此通道没有可用的新版本。" : "新版本 " + _updateOffer.Version + " 已可下载。";
        if (_selected == "advanced") BuildSections();
    }

    private void UpdateAutomaticState()
    {
        bool changed = false;
        if (_automaticInstalling is { IsCompleted: true } operation)
        {
            _automaticInstalling = null;
            // A progress read started before terminal publication must not replace the
            // operation's final status after it completes, including rollback.
            _automaticDiscardReading = _automaticReading is not null;
            if (operation.IsCompletedSuccessfully)
            {
                _automaticStatus = operation.Result;
                _updateStatus = _automaticStatus.Phase == "rolledback"
                    ? "已回滚更新。" : "更新完成。";
                if (UpdateNeedsRestart) _updateStatus += "重新启动后生效。";
            }
            else { _ = operation.Exception; _updateStatus = "更新未完成。可以继续更新，或下载安装包。"; }
            changed = true;
        }
        if (_automaticReading is { IsCompleted: true } reading)
        {
            _automaticReading = null;
            if (_automaticDiscardReading) { _ = reading.Exception; _automaticDiscardReading = false; }
            else if (reading.IsCompletedSuccessfully)
            {
                changed |= _automaticStatus != reading.Result;
                _automaticStatus = reading.Result;
                if (_automaticInstalling is null && _automaticStatus.Phase is "complete" or "rolledback")
                {
                    _updateStatus = _automaticStatus.Phase == "rolledback" ? "已回滚更新。" : "更新完成。";
                    if (UpdateNeedsRestart) _updateStatus += "重新启动后生效。";
                }
                if (!_automaticRecoveryAttempted && _automaticInstalling is null && _automaticStatus.CanInstall && _automaticStatus.Version is { } version
                    && _automaticStatus.Channel is { } channel && _automaticStatus.Phase is "verifying" or "downloading" or "preparing" or "activating" or "paused")
                {
                    _automaticRecoveryAttempted = true;
                    _automaticInstalling = _automaticUpdates!.InstallAsync(version, channel, _updateStop.Token);
                    WakeOnPlatformCompletion(_automaticInstalling);
                }
            }
            else { _ = reading.Exception; _updateStatus = "此安装位置无法自动更新，请使用系统安装包。"; changed = true; }
            if (AutomaticUpdateInProgress)
            {
                _automaticRefreshDelay = Task.Delay(500, _updateStop.Token);
                WakeOnPlatformCompletion(_automaticRefreshDelay);
            }
        }
        if (AutomaticUpdateInProgress && _automaticReading is null && _automaticRefreshDelay is not { IsCompleted: false })
        { _automaticRefreshDelay = null; _automaticReading = _automaticUpdates!.ReadAsync(_updateStop.Token); WakeOnPlatformCompletion(_automaticReading); }
        if (changed && _selected == "advanced") BuildSections();
    }

    private void BuildUpdateCard()
    {
        if (_updateQuery is null) return;
        var card = SettingsCard("SettingsUpdateCard", new(20, 18, 20, 18), spacing: 10, radius: 18);
        Text(card, "NexaCL " + _updateQuery.CurrentVersion, 19, Ink, 28, 600);
        Text(card, _updateStatus, 13, Muted, 22);
        if (AutomaticUpdateInProgress)
            Text(card, _automaticStatus?.Phase switch { "downloading" => "正在下载并校验", "preparing" => "正在准备新版本", "activating" => "正在切换版本", _ => "正在验证发布信息" }, 13, Muted, 22);
        var actions = Stack(card, "SettingsUpdateActions", XsrUiOrientation.Horizontal, 10);
        var check = ActionButton(actions, "SettingsCheckUpdate", _updateReading is null ? "检查更新" : "正在检查", CheckUpdate, 100);
        _shell.Tree.GetComponent<XsrUiInput>(check)!.Enabled = _updateReading is null;
        if (_updateOffer is not null)
        {
            if (_automaticStatus?.CanInstall == true && !AutomaticUpdateInProgress && _updateQuery.Channel is "alpha" or "beta" or "stable")
                ActionButton(actions, "SettingsInstallUpdate", "立即更新", InstallUpdate, 100);
            ActionButton(actions, "SettingsDownloadUpdate", "下载安装包", DownloadUpdate, 112);
            ActionButton(actions, "SettingsPortableUpdate", "便携包", PortableUpdate, 80);
            ActionButton(actions, "SettingsUpdateNotes", "GitHub / 更新日志", UpdateNotes, 144);
        }
        if (UpdateNeedsRestart && _automaticInstalling is null)
            ActionButton(actions, "SettingsRestartUpdate", "重新启动", RestartUpdate, 100);
        else if (_updateOffer is null && _automaticStatus?.Version is not null && _automaticStatus.CanInstall
            && _automaticStatus.Channel is "alpha" or "beta" or "stable"
            && _automaticStatus.Phase is not ("complete" or "rolledback") && _automaticInstalling is null)
            ActionButton(actions, "SettingsResumeUpdate", "继续更新", InstallUpdate, 100);
        if (_automaticStatus?.CanRollback == true && !AutomaticUpdateInProgress)
            ActionButton(actions, "SettingsRollbackUpdate", "回滚更新", RollbackUpdate, 100);
    }
}
