using Nexa.Services.Settings;
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
    private NexaUpdateQuery? _buildUpdateQuery, _updateReadingQuery;
    private Task<XsrResult<SettingsEffectiveSnapshot>>? _updatePolicyReading;
    private long _updatePolicyRevision = -1;
    private bool _startupUpdateChecked;
    private CancellationTokenSource? _updateCheckStop;
    private string? _updateOfferChannel;
    private Action<Uri>? _openUpdateLink;
    private Task<XsrResult<NexaUpdateStatus>>? _updateReading;
    private NexaUpdateOffer? _updateOffer;
    private string _updateStatus = "通过 Cloudflare 检查当前平台的新版本。";
    private readonly CancellationTokenSource _updateStop = new();

    internal void ConfigureUpdates(XsrQueryRouter queries, NexaUpdateQuery query, Action<Uri> open,
        IAutomaticUpdateControl? automatic = null, Action? restart = null)
    {
        _updateQueries = queries; _updateQuery = _buildUpdateQuery = query; _openUpdateLink = open;
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
            string? channel = _updateOffer is null ? _automaticStatus.Channel : _updateOfferChannel;
            if (version is null || channel is not ("alpha" or "beta" or "stable")) return;
            _automaticRecoveryAttempted = true;
            _automaticInstalling = _automaticUpdates.InstallAsync(version, channel, _updateStop.Token);
            WakeOnPlatformCompletion(_automaticInstalling); _updateStatus = "正在更新…"; BuildSections(); return;
        }
        if (id == RollbackUpdate && !AutomaticUpdateInProgress && _automaticStatus?.CanRollback == true && _automaticUpdates is not null)
        { _automaticRecoveryAttempted = true; _automaticInstalling = _automaticUpdates.RollbackAsync(_updateStop.Token); WakeOnPlatformCompletion(_automaticInstalling); return; }
        if (id == CheckUpdate && _updateReading is null && StartUpdateCheck())
        {
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
        UpdateDiscoveryPreferences();
        UpdateAutomaticState();
        if (_updateReading is not { IsCompleted: true } task) return;
        _updateReading = null;
        _updateCheckStop?.Dispose(); _updateCheckStop = null;
        if (_updateReadingQuery != _updateQuery) { _ = task.Exception; return; }
        _updateOffer = PendingQuery.Succeeded(task) ? task.Result.Value!.Offer : null;
        _updateOfferChannel = _updateOffer is null ? null : _updateReadingQuery?.Channel;
        _updateStatus = !PendingQuery.Succeeded(task) ? "暂时无法检查更新，请重试。"
            : _updateOffer is null ? "此通道没有可用的新版本。" : "新版本 " + _updateOffer.Version + " 已可下载。";
        if (_selected == "advanced") BuildSections();
    }

    private bool StartUpdateCheck()
    {
        if (_updateQuery is null || _updateQueries?.TryResolve(NexaUpdateContract.Check, out var route) != true) return false;
        _updateCheckStop = CancellationTokenSource.CreateLinkedTokenSource(_updateStop.Token);
        _updateReadingQuery = _updateQuery;
        _updateStatus = "正在检查更新…";
        _updateReading = _updateQueries.QueryAsync<NexaUpdateQuery, NexaUpdateStatus>(route, _updateReadingQuery, cancellationToken: _updateCheckStop.Token).AsTask();
        WakeOnPlatformCompletion(_updateReading);
        return true;
    }

    private void UpdateDiscoveryPreferences()
    {
        if (_buildUpdateQuery is null || _instanceDirectory is not null) return;
        long revision = _store.Read<long>(_revisionId).Value;
        if (_updatePolicyReading is null && _updatePolicyRevision != revision && _queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var route))
        {
            _updatePolicyReading = _queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(route, new()).AsTask();
            WakeOnPlatformCompletion(_updatePolicyReading);
        }
        if (_updatePolicyReading is not { IsCompleted: true } reading) return;
        _updatePolicyReading = null;
        if (!PendingQuery.Succeeded(reading)) { _ = reading.Exception; _updatePolicyRevision = revision; return; }
        var snapshot = reading.Result.Value!;
        if (snapshot.Revision != revision) return;
        _updatePolicyRevision = snapshot.Revision;
        var policy = SettingsUpdatePolicy.FromSnapshot(snapshot, _buildUpdateQuery.Channel);
        var query = _buildUpdateQuery with { Channel = policy.Channel };
        bool changed = query != _updateQuery;
        if (changed)
        {
            _updateCheckStop?.Cancel(); _updateCheckStop?.Dispose(); _updateCheckStop = null;
            if (_updateReading is { } retired)
                _ = retired.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _updateReading = null; _updateReadingQuery = null;
            _updateQuery = query; _updateOffer = null; _updateOfferChannel = null;
            if (!AutomaticUpdateInProgress && !UpdateNeedsRestart)
                _updateStatus = "通过 Cloudflare 检查当前平台的新版本。";
        }
        if (!_startupUpdateChecked)
        {
            _startupUpdateChecked = true;
            if (policy.AutomaticCheck) StartUpdateCheck();
        }
        if (changed && _visible && _selected == "advanced" && _catalog is not null) BuildSections();
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
        var header = Stack(card, "SettingsUpdateHeader", XsrUiOrientation.Horizontal, 12);
        var title = Text(header, Program.ProductDisplayTitle(_updateQuery.CurrentVersion), 19, Ink, 28, 600);
        _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
        var check = RefreshIcon(header, "SettingsCheckUpdate", CheckUpdate);
        _shell.Tree.GetComponent<XsrUiInput>(check)!.Enabled = _updateReading is null;
        Text(card, _updateStatus, 13, Muted, 22);
        if (AutomaticUpdateInProgress)
            Text(card, _automaticStatus?.Phase switch { "downloading" => "正在下载并校验", "preparing" => "正在准备新版本", "activating" => "正在切换版本", _ => "正在验证发布信息" }, 13, Muted, 22);
        var actions = Stack(card, "SettingsUpdateActions", XsrUiOrientation.Horizontal, 10);
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
