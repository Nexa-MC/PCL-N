using System.Globalization;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private InstanceContentIntegrityQuery? _contentIntegrityQuery;
    private InstanceContentIntegrity? _contentIntegrity;
    private Task<XsrResult<InstanceContentIntegrity>>? _contentIntegrityRead;
    private CancellationTokenSource? _contentIntegrityStop;
    private string? _contentIntegrityError, _contentIntegritySourceCaption;
    private XsrUiEntityId _contentIntegritySection, _contentIntegritySource;

    private void BuildContentTrustStates()
    {
        if (_instanceDirectory is not null || _selected != "privacy") return;
        var card = FormGroup(_sections, "ContentTrustStates", "内容信任与完整性");
        void Rule(string heading, string description)
        {
            Text(card, heading, 13, Ink, 28, 600);
            ContentName(card, description, 12, null, maxLines: 0, foreground: Muted, literal: false);
        }
        Rule("关联来源", "来源关联只匹配当前文件摘要与已识别的在线文件记录，不证明原始下载来源。");
        Rule("下载文件校验", "已知资源站下载必须通过已发布的长度和摘要校验；本地内容 SHA-256 可在实例内容详情查看。");
        Rule("修改状态", "修改状态只比较已保存的受管理更新摘要；首次读取不会建立可信基准。");
        ContentName(card, "无更新基准时，修改状态显示未知。", 12, null, maxLines: 0, foreground: Muted, literal: false);
        ContentName(card, "在主页打开版本设置，再进入模组、资源包或光影包的内容详情，查看实际 SHA-256、关联来源和修改状态。", 12, null, maxLines: 0, foreground: Muted, literal: false);
        ContentName(card, "未知来源保留；更新仍需已识别来源和兼容版本，发布前校验文件与依赖。", 11, null, maxLines: 0, foreground: Muted, literal: false);
    }

    private void CancelContentIntegrity()
    {
        _contentIntegrityStop?.Cancel(); _contentIntegrityStop?.Dispose(); _contentIntegrityStop = null;
        _contentIntegrityRead = null; _contentIntegrityQuery = null; _contentIntegrity = null;
        _contentIntegrityError = _contentIntegritySourceCaption = null; _contentIntegritySection = _contentIntegritySource = default;
    }

    private InstanceContentIntegrityQuery? ContentIntegrityFile(InstanceContentEntry item) => _instance is not null && !item.IsDirectory && item.Size is { } size
        && _selected is "mods" or "resourcepacks" or "shaderpacks" or "screenshots" or "schematics"
        ? new(_instance, _selected, item.Name, size, item.ModifiedUtcTicks) : null;

    private void BuildContentIntegrity(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (ContentIntegrityFile(item) is not { } query) return;
        if (query != _contentIntegrityQuery)
        {
            CancelContentIntegrity(); _contentIntegrityQuery = query;
            if (_queries.TryResolve(InstanceContentIntegrityContract.Read, out var route))
            {
                _contentIntegrityStop = new();
                _contentIntegrityRead = _queries.QueryAsync<InstanceContentIntegrityQuery, InstanceContentIntegrity>(route, query, cancellationToken: _contentIntegrityStop.Token).AsTask();
                WakeOnPlatformCompletion(_contentIntegrityRead);
            }
            else _contentIntegrityError = "此环境未提供内容完整性读取。";
        }
        _contentIntegritySection = Stack(parent, "ContentIntegrity", XsrUiOrientation.Vertical, 8);
        RenderContentIntegrity();
    }

    private void UpdateContentIntegrity()
    {
        if (_contentIntegrityQuery is not { } query) return;
        if (_contentDetail is not { } item || ContentIntegrityFile(item) != query) { CancelContentIntegrity(); return; }
        if (_contentIntegrityRead is { IsCompleted: true } read)
        {
            _contentIntegrityRead = null; _contentIntegrityStop?.Dispose(); _contentIntegrityStop = null;
            if (PendingQuery.Succeeded(read) && read.Result.Value!.File == query) _contentIntegrity = read.Result.Value;
            else _contentIntegrityError = "无法读取完整性，文件可能已变化。请刷新内容列表。";
            if (_contentIntegritySection.IsAssigned && _shell.Tree.IsAlive(_contentIntegritySection)) RenderContentIntegrity();
        }
        UpdateContentIntegritySource();
    }

    private void RenderContentIntegrity()
    {
        foreach (var child in _shell.Tree.Children(_contentIntegritySection).ToArray())
        {
            _shell.Tree.Walk(child, entity => { _managementActions.Remove(entity); _contentActions.Remove(entity); return true; });
            _shell.Tree.Destroy(child);
        }
        Text(_contentIntegritySection, "内容完整性", 15, Ink, 30, 600);
        if (_contentIntegrity is not { } fact)
        {
            Text(_contentIntegritySection, _contentIntegrityError ?? "正在重新核对文件并计算摘要…", 12, Muted, 34);
            return;
        }
        IntegrityFact("Sha256", "SHA-256", fact.Sha256);
        IntegrityFact("Captured", "摘要捕获时间", fact.CapturedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture));
        IntegrityFact("Modified", "修改状态", _shell.Renderer.LocalizeText(fact.Modified switch
        {
            false => "与受管理更新基准一致",
            true => "与受管理更新基准不同",
            _ => fact.BaselineState == InstanceContentBaselineState.Incomplete ? "未知（更新记录不完整）" : "未知（没有受管理更新基准）"
        }));
        if (fact.BaselineSha256 is { } baseline) IntegrityFact("Baseline", "更新基准 SHA-256", baseline);
        if (fact.BaselineTransactionId is { } transaction) IntegrityFact("Transaction", "基准事务", transaction.ToString("N"));
        _contentIntegritySourceCaption = ContentIntegritySourceCaption();
        _contentIntegritySource = IntegrityFact("Source", "关联来源", _contentIntegritySourceCaption);
        Text(_contentIntegritySection, "未知来源保留；更新仍需已识别来源和兼容版本，发布前校验文件与依赖。", 11, Muted, 40);
        Text(_contentIntegritySection, "修改状态只比较已保存的受管理更新摘要；首次读取不会建立可信基准。", 11, Muted, 40);
        var query = _contentIntegrityQuery;
        ManagementButton(_contentIntegritySection, "重新读取完整性", () =>
        { if (query != _contentIntegrityQuery) return; CancelContentIntegrity(); BuildSections(true); }, 136);
    }

    private XsrUiEntityId IntegrityFact(string key, string label, string value)
    {
        var row = Stack(_contentIntegritySection, "ContentIntegrity." + key, XsrUiOrientation.Horizontal, 12);
        _shell.Tree.GetComponent<XsrUiElement>(Text(row, label, 12, Muted, 28))!.Width = 132;
        var text = DiagnosticText(row, "ContentIntegrity." + key + ".Value", value, 12, Ink, 28);
        _shell.Tree.GetComponent<XsrUiElement>(text)!.Weight = 1; return text;
    }

    private string ContentIntegritySourceCaption()
    {
        if (_contentIntegrity is not { } fact || _contentIntegrityQuery is not { } query) return "";
        var online = _onlineQuery is { } current && current.InstanceDirectory == query.InstanceDirectory && current.PageId == query.PageId
            && current.Name == query.Name && current.ExpectedSize == query.ExpectedSize && current.ExpectedModifiedUtcTicks == query.ExpectedModifiedUtcTicks ? _onlineContent : null;
        if (online is null && _contentDetail is { } item && OnlineFile(item) is { } file) _onlineList.TryGetValue(file, out online);
        var matches = online?.InstalledFiles.Where(match => match.FileName == query.Name && match.Sha512.Equals(fact.Sha512, StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Source.Provider + ":" + match.Source.ProjectId + "@" + match.VersionId).Distinct().Take(8).ToArray();
        return matches is { Length: > 0 } ? string.Join(" · ", matches) : _shell.Renderer.LocalizeText("未知（尚无匹配当前摘要的来源关联）");
    }

    private void UpdateContentIntegritySource()
    {
        if (_contentIntegrity is null || !_contentIntegritySource.IsAssigned || !_shell.Tree.IsAlive(_contentIntegritySource)) return;
        string caption = ContentIntegritySourceCaption(); if (_contentIntegritySourceCaption == caption) return;
        _contentIntegritySourceCaption = caption; _shell.Tree.SetComponent(_contentIntegritySource, new XsrUiText(caption) { Localize = false });
    }
}
