using Nexa.Services.Downloads;
using Nexa.Services.Network;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId NetworkTraceRefresh = XsrSemanticId.Parse("ui.settings.network.trace.refresh");
    private static readonly XsrSemanticId NetworkProbeStart = XsrSemanticId.Parse("ui.settings.network.probe.start");
    private Task<XsrResult<IReadOnlyList<NetworkRequestTrace>>>? _networkTraceReading;
    private IReadOnlyList<NetworkRequestTrace> _networkTraceRows = [];
    private bool _networkTraceLoaded;
    private CancellationTokenSource? _networkProbeStop;
    private Task<XsrResult<NetworkManualProbeSnapshot>>? _networkProbeReading;
    private NetworkManualProbeSnapshot? _networkProbeSnapshot;
    private readonly HashSet<XsrUiEntityId> _networkProbeActions = [];
    private Task<XsrResult<ResourceNetworkPolicySnapshot>>? _resourceResolutionReading;
    private ResourceNetworkPolicySnapshot? _resourceResolutionSnapshot;
    private long _resourceResolutionRevision = -1;
    private long _networkDownloadRevision = -1;
    private DateTimeOffset _networkDownloadObservedAt;
    private DownloadTransferView[] _networkDownloadRows = [];

    private void BuildNetworkDiagnostics()
    {
        if (_instanceDirectory is not null || _selected != "network") return;
        _networkProbeActions.Clear();
        var manual = FormGroup(_sections, "NetworkProbe", "网络故障诊断");
        Text(manual, "探测固定来源的 HTTPS 响应，不发送账户信息。结果只表示观察时刻。", 11, Muted, 32);
        var start = ActionButton(manual, "NetworkProbe.Start", _networkProbeReading is null ? "开始网络探测" : "正在网络探测", NetworkProbeStart, 132);
        _shell.Tree.GetComponent<XsrUiInput>(start)!.Enabled = _networkProbeReading is null;
        _networkProbeActions.Add(start);
        if (_networkProbeSnapshot is { } probed)
            foreach (var observed in probed.Endpoints) EndpointRow(manual, observed.Host, observed.ObservedAt, observed.StatusCode, observed.ElapsedMilliseconds, observed.ErrorKind);
        var endpoints = FormGroup(_sections, "NetworkObservedEndpoints", "最近观察的 Endpoint 状态");
        Text(endpoints, "来自最近已记录请求；不是当前健康状态。", 11, Muted, 32);
        foreach (var observed in _networkTraceRows.GroupBy(entry => entry.Host, StringComparer.Ordinal).Select(group => group.MaxBy(entry => entry.Sequence)!).Take(16))
            EndpointRow(endpoints, observed.Host, observed.Timestamp, observed.StatusCode, observed.ElapsedMilliseconds, observed.ErrorKind);
        if (_networkTraceRows.Count == 0) Text(endpoints, "尚未观察到请求结果。", 11, Muted, 28);
        var mirrors = FormGroup(_sections, "NetworkMirrorResolution", "Mirror Resolution");
        ReadResourceResolution();
        string source = _values?.Values.FirstOrDefault(item => item.Key == "network.resource-source")?.Value.Value ?? "follow-request";
        Text(mirrors, source switch
        { "official-first" => "资源站使用官方优先；地域与来源禁用策略仍生效。", "mirrors-first" => "资源站使用镜像优先；地域与来源禁用策略仍生效。", _ => "资源站跟随页面所选来源顺序；地域与来源禁用策略仍生效。" }, 11, Muted, 32);
        if (_resourceResolutionSnapshot?.LastResolution is { } resolution)
        {
            string observed = resolution.Provider + " · " + resolution.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                + " · " + string.Join(" → ", resolution.CandidateHosts) + " · " + (resolution.SelectedHost ?? "—");
            DesktopLiteralText.Preserve(_shell.Tree, Text(mirrors, observed, 11, Muted, 28));
        }
        else Text(mirrors, "尚未观察到资源站来源解析。", 11, Muted, 28);
        var downloads = FormGroup(_sections, "NetworkDownloadDiagnostics", "Download Diagnostics");
        Text(downloads, "活动传输的最近观察；完成的传输会从列表移除。", 11, Muted, 32);
        if (_networkDownloadRows.Length == 0) Text(downloads, "当前没有活动下载。", 11, Muted, 28);
        foreach (var transfer in _networkDownloadRows)
        {
            string host = Uri.TryCreate(transfer.Source, UriKind.Absolute, out var address) && address.Scheme is "https" or "http" ? address.IdnHost : "—";
            string row = _networkDownloadObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                + " · " + host + " · " + transfer.Stage + " · " + transfer.DownloadedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + (transfer.TotalBytes >= 0 ? transfer.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—")
                + " B · " + transfer.BytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B/s";
            DesktopLiteralText.Preserve(_shell.Tree, Text(downloads, row, 11, Muted, 28));
        }
        var panel = FormGroup(_sections, "NetworkDiagnostics", "网络诊断记录");
        Text(panel, "开启请求记录或自动诊断后可查看。仅保留主机、结果和耗时。", 11, Muted, 32);
        ActionButton(panel, "NetworkDiagnostics.Refresh", "刷新记录", NetworkTraceRefresh, 112);
        foreach (var entry in _networkTraceRows.Reverse().Take(32))
        {
            var row = Text(panel, $"{entry.Timestamp.ToLocalTime():HH:mm:ss} · {entry.Host} · {entry.Kind} · "
                + (entry.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? entry.ErrorKind ?? "")
                + $" · {entry.ElapsedMilliseconds:F1} ms", 11, Muted, 28);
            DesktopLiteralText.Preserve(_shell.Tree, row);
        }
        if (_networkTraceLoaded && _networkTraceRows.Count == 0) Text(panel, "没有网络诊断记录。", 11, Muted, 28);
    }

    private void EndpointRow(XsrUiEntityId parent, string host, DateTimeOffset at, int? status, double elapsed, string? error)
    {
        string value = host + " · " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            + " · " + (status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? error ?? "")
            + " · " + elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        DesktopLiteralText.Preserve(_shell.Tree, Text(parent, value, 11, Muted, 28));
    }

    private void StartNetworkProbe(DesktopUiIntent intent)
    {
        if (!_visible || _disposed || _instanceDirectory is not null || _selected != "network" || _networkProbeReading is not null
            || !_networkProbeActions.Contains(intent.Source) || !_shell.Tree.IsAlive(intent.Source)
            || _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != true
            || !_queries.TryResolve(NetworkDiagnosticsContract.Probe, out var route)) return;
        _networkProbeStop = new();
        _networkProbeReading = _queries.QueryAsync<NetworkManualProbeQuery, NetworkManualProbeSnapshot>(route, new(), cancellationToken: _networkProbeStop.Token).AsTask();
        WakeOnPlatformCompletion(_networkProbeReading);
        BuildSections();
    }

    private void CancelNetworkProbe()
    {
        _networkProbeStop?.Cancel(); _networkProbeStop?.Dispose(); _networkProbeStop = null;
        _networkProbeReading = null; _networkProbeActions.Clear();
    }

    private void ReadNetworkDiagnostics()
    {
        if (!_visible || _instanceDirectory is not null || _selected != "network" || _networkTraceReading is not null) return;
        if (_queries.TryResolve(NetworkDiagnosticsContract.Trace, out var route))
        {
            _networkTraceReading = _queries.QueryAsync<NetworkTraceQuery, IReadOnlyList<NetworkRequestTrace>>(route, new()).AsTask();
            WakeOnPlatformCompletion(_networkTraceReading);
        }
        _resourceResolutionRevision = -1; ReadResourceResolution();
    }
    private void ReadResourceResolution()
    {
        if (_resourceResolutionReading is not null || _resourceResolutionRevision == (_values?.Revision ?? 0)
            || _resourceQueries is null || !_resourceQueries.TryResolve(ResourceCatalogContract.NetworkPolicy, out var route)) return;
        _resourceResolutionRevision = _values?.Revision ?? 0;
        _resourceResolutionReading = _resourceQueries.QueryAsync<ResourceNetworkPolicyQuery, ResourceNetworkPolicySnapshot>(route, new()).AsTask();
        WakeOnPlatformCompletion(_resourceResolutionReading);
    }

    private void UpdateNetworkDiagnostics()
    {
        if (!_visible || _instanceDirectory is not null || _selected != "network") { CancelNetworkProbe(); return; }
        if (_store.TryResolve(DownloadStateContract.TransfersKey, out var transfers))
        {
            var active = _store.ReadCollection<DownloadTransferView>(transfers);
            if (active.Revision != _networkDownloadRevision)
            {
                _networkDownloadRevision = active.Revision;
                _networkDownloadObservedAt = DateTimeOffset.UtcNow;
                _networkDownloadRows = active.Items.Take(16).ToArray();
                if (_catalog is not null) BuildSections();
            }
        }
        if (_resourceResolutionReading is { IsCompleted: true } resolutionRead)
        {
            _resourceResolutionReading = null;
            if (resolutionRead.IsCompletedSuccessfully && resolutionRead.Result.IsSuccess) _resourceResolutionSnapshot = resolutionRead.Result.Value!;
            if (_catalog is not null) BuildSections();
        }
        if (_networkProbeReading is { IsCompleted: true } probe)
        {
            _networkProbeReading = null; _networkProbeStop?.Dispose(); _networkProbeStop = null;
            if (probe.IsCompletedSuccessfully && probe.Result.IsSuccess) _networkProbeSnapshot = probe.Result.Value!;
            else if (!probe.IsCanceled) _feedback.Error("网络探测未完成。");
            if (_catalog is not null) BuildSections();
        }
        if (_networkTraceReading is not { IsCompleted: true } read) return;
        _networkTraceReading = null;
        if (!_visible || _selected != "network") return;
        if (read.IsCompletedSuccessfully && read.Result.IsSuccess) _networkTraceRows = read.Result.Value!;
        else _feedback.Error("无法读取网络诊断记录。");
        _networkTraceLoaded = true;
        if (_catalog is not null) BuildSections();
    }
}
