using Nexa.Services.Minecraft.Management;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private XsrQueryRouter? _resourceQueries;
    private Action<Uri>? _openResourceLink;
    private ResourceContentOnlineQuery? _onlineQuery;
    private ResourceContentOnline? _onlineContent;
    private Task<XsrResult<ResourceContentOnline>>? _onlineRead;
    private CancellationTokenSource? _onlineStop;
    private XsrUiEntityId _onlineSection;
    private string? _onlineError;
    private Task<XsrResult<ResourceIconResult>>? _onlineIconRead;
    private Nexa.Core.Media.PngImage? _onlineIcon;
    private XsrCommandRouter? _resourceCommands;
    private string? _onlineListInstance, _onlineListPage;
    private CancellationTokenSource? _onlineListStop;
    private readonly List<(ResourceContentOnlineQuery[] Files, Task<XsrResult<ResourceContentOnlineBatch>> Read)> _onlineListReads = [];
    private bool _onlineListRefresh;
    private XsrUiEntityId _onlineListStatus;
    private int _onlineListTotal = -1, _onlineListMatched;
    private bool _onlineListIncomplete;
    private bool _onlineListFailed;
    private readonly Dictionary<ResourceContentOnlineQuery, ResourceContentOnline> _onlineList = [];
    private readonly Dictionary<string, Nexa.Core.Media.PngImage?> _onlineListIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<XsrResult<ResourceIconResult>>> _onlineListIconReads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XsrUiEntityId> _contentIconEntities = new(StringComparer.Ordinal);
    private string? _onlinePresentationLanguage;

    internal void ConfigureOnlineContent(XsrQueryRouter queries, Action<Uri> open, XsrCommandRouter? commands = null)
    { _resourceQueries = queries; _openResourceLink = open; _resourceCommands = commands; }

    private void CancelOnlineList()
    {
        _selectedContentUpdates.Clear();
        _onlineListStop?.Cancel(); _onlineListStop?.Dispose(); _onlineListStop = null;
        foreach (var pending in _onlineListReads)
            _ = pending.Read.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _onlineListReads.Clear(); _onlineListRefresh = false;
        _onlineListTotal = -1; _onlineListMatched = 0; _onlineListIncomplete = false;
        _onlineListInstance = _onlineListPage = null; _onlineList.Clear(); _onlineListFailed = false;
        _onlineListIcons.Clear(); _onlineListIconReads.Clear();
        _contentIconEntities.Clear();
    }
    private ResourceContentOnlineQuery? OnlineFile(InstanceContentEntry item) => _instance is not null && item.Size is { } size && !item.IsDirectory
        ? new(_instance, _selected, item.Name, size, item.ModifiedUtcTicks) : null;
    private InstanceContentEntry LinkedContent(InstanceContentEntry item)
    {
        if (OnlineFile(item) is not { } key || !_onlineList.TryGetValue(key, out var online) || online.Project is null) return item;
        var icon = online.Project.IconUrl is { } url ? _onlineListIcons.GetValueOrDefault(url) : null;
        return item with
        {
            DisplayName = DesktopResourceText.Name(online.Project, _store),
            Version = _selected == "mods" ? online.InstalledVersion ?? item.Version : item.Version,
            Icon = item.Icon ?? icon,
            UpdateAvailable = online.UpdateAvailable ?? item.UpdateAvailable,
            UpdateVersion = online.UpdateVersion?.Number ?? item.UpdateVersion
        };
    }
    private void UpdateOnlineList()
    {
        if (_onlineListInstance != _instance || _onlineListPage != _selected) { CancelOnlineList(); _onlineListInstance = _instance; _onlineListPage = _selected; }
        if (_instance is null || _selected is not ("mods" or "resourcepacks" or "shaderpacks") || !_managementLoaded || _management is null || _managementWrite is not null
            || _resourceQueries?.TryResolve(ResourceCatalogContract.ContentOnlineBatch, out var route) != true) return;
        bool changed = false;
        foreach (var pending in _onlineListReads.ToArray())
        {
            var read = pending.Read;
            if (!read.IsCompleted) continue;
            _onlineListReads.Remove(pending); changed = true;
            if (PendingQuery.Succeeded(read))
            {
                foreach (var match in read.Result.Value!.Matches)
                    if (pending.Files.Contains(match.File)) _onlineList[match.File] = match.Content;
            }
            else { _ = read.Exception; _onlineListFailed = true; }
            foreach (var file in pending.Files)
                if (!_onlineList.ContainsKey(file)) _onlineList[file] = new(null, null, [], "暂时无法识别在线信息，已保留本地资料。");
        }
        if (changed)
        {
            _onlineListMatched = _onlineList.Count(pair => pair.Value.Project is not null);
            _onlineListIncomplete = _onlineList.Any(pair => pair.Value.Notice is { Length: > 0 });
            ApplyContentFilter(); UpdateContentWindow();
        }
        UpdateOnlineListIcons();
        if (_onlineListTotal < 0) _onlineListTotal = _management.Contents.FirstOrDefault(p => p.PageId == _selected)?.Entries.Count(item => OnlineFile(item) is not null) ?? 0;
        if (_onlineListReads.Count >= 2 || _onlineList.Count >= _onlineListTotal) { UpdateOnlineListStatus(); return; }
        var visible = _contentSnapshot?.Entries.Skip(Math.Max(0, _contentWindowStart * Math.Max(1, _contentColumns))).Take(_contentWindowCount * Math.Max(1, _contentColumns)) ?? [];
        var candidates = visible.Concat(_management.Contents.FirstOrDefault(p => p.PageId == _selected)?.Entries ?? []).Select(OnlineFile)
            .Where(q => q is not null).Select(q => q!).Distinct().ToArray();
        while (_onlineListReads.Count < 2)
        {
            var files = candidates.Where(q => !_onlineList.ContainsKey(q) && !_onlineListReads.Any(p => p.Files.Contains(q))).Take(2).ToArray();
            if (files.Length == 0) break;
            _onlineListStop ??= new();
            var read = _resourceQueries.QueryAsync<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(route, new(files) { Refresh = _onlineListRefresh }, cancellationToken: _onlineListStop.Token).AsTask();
            _onlineListReads.Add((files, read)); WakeOnPlatformCompletion(read);
        }
        UpdateOnlineListStatus();
    }

    private void UpdateOnlineListStatus()
    {
        if (!_onlineListStatus.IsAssigned || !_shell.Tree.IsAlive(_onlineListStatus)) return;
        int total = Math.Max(0, _onlineListTotal);
        string label = _resourceQueries?.TryResolve(ResourceCatalogContract.ContentOnlineBatch, out _) != true ? "在线信息尚不可用"
            : _onlineListReads.Count > 0 || _onlineList.Count < total ? "正在关联在线信息"
            : _onlineListFailed || _onlineListIncomplete ? "在线信息已刷新，部分文件未能完整识别" : "在线信息已刷新";
        string content = _shell.Renderer.LocalizeText(label) + $" · {_onlineList.Count}/{total} · " + _shell.Renderer.LocalizeText("已关联") + " " + _onlineListMatched;
        if (_shell.Tree.GetComponent<XsrUiText>(_onlineListStatus)!.Content == content) return;
        _shell.Tree.SetComponent(_onlineListStatus, new XsrUiText(content));
        _shell.Tree.MarkDirty(_onlineListStatus, XsrUiDirtyKinds.Paint);
    }

    private void UpdateOnlineListIcons()
    {
        bool changed = false;
        foreach (var (url, read) in _onlineListIconReads.ToArray())
        {
            if (!read.IsCompleted) continue;
            _onlineListIconReads.Remove(url);
            if (_onlineListIcons.Count >= 256) _onlineListIcons.Remove(_onlineListIcons.Keys.First());
            _onlineListIcons[url] = PendingQuery.Succeeded(read) ? read.Result.Value?.Image : null;
            changed = true;
        }
        if (changed && _contentSnapshot is { } snapshot)
        {
            _contentSnapshot = snapshot with { Entries = snapshot.Entries.Select(LinkedContent).ToArray() };
            foreach (var item in _contentSnapshot.Entries)
                if (item.Icon is { } image && _contentIconEntities.TryGetValue(item.Name, out var entity) && _shell.Tree.IsAlive(entity)
                    && _shell.Tree.GetComponent<XsrUiImage>(entity) is { Raster: null } target)
                {
                    target.Raster = new(image, [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))]) { FitToBounds = true };
                    _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
                }
        }
        if (_contentDetail is not null || _contentSnapshot is null || _contentWindowStart < 0
            || _resourceQueries?.TryResolve(ResourceCatalogContract.Icon, out var route) != true) return;
        // Only visible virtualized rows request icons. Missing/failed previews are cached too.
        foreach (var item in _contentSnapshot.Entries.Skip(_contentWindowStart).Take(_contentWindowCount))
        {
            if (_onlineListIconReads.Count >= 4) break;
            if (item.Icon is not null || OnlineFile(item) is not { } key || !_onlineList.TryGetValue(key, out var online)
                || online.Project?.IconUrl is not { } url || _onlineListIcons.ContainsKey(url) || _onlineListIconReads.ContainsKey(url)) continue;
            _onlineListStop ??= new();
            var read = _resourceQueries.QueryAsync<ResourceIconQuery, ResourceIconResult>(route, new(url), cancellationToken: _onlineListStop.Token).AsTask();
            _onlineListIconReads[url] = read;
            WakeOnPlatformCompletion(read);
        }
    }

    private void UpdateOnlinePack(ResourceContentOnlineQuery query, ResourceVersion version)
    {
        if (_instance != query.InstanceDirectory || _selected != query.PageId || _managementWrite is not null
            || _resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContent, out var route) != true) return;
        _managementWriteInstance = _instance;
        _managementWrite = _resourceCommands.Dispatch(route, new ResourceContentUpdateCommand(query, new(version.Provider, version.ProjectId), version.Id)).Completion;
        CancelOnlineList(); WakeOnPlatformCompletion(_managementWrite);
        RenderOnlineContent();
    }

    private void CancelOnlineContent()
    {
        CancelContentIntegrity();
        _contentChangelogRead = null; _contentChangelog = null;
        _onlineStop?.Cancel(); _onlineStop?.Dispose(); _onlineStop = null;
        _onlineRead = null; _onlineQuery = null; _onlineContent = null; _onlineError = null; _onlineSection = default;
        _onlineIconRead = null; _onlineIcon = null;
    }

    private void BuildOnlineContent(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (_instance is null || item.IsDirectory || item.Size is not { } size || _selected is not ("mods" or "resourcepacks" or "shaderpacks")
            || _resourceQueries?.TryResolve(ResourceCatalogContract.ContentOnline, out var route) != true) return;
        var query = new ResourceContentOnlineQuery(_instance, _selected, item.Name, size, item.ModifiedUtcTicks);
        if (query != _onlineQuery)
        {
            CancelOnlineContent(); _onlineQuery = query; _onlineStop = new();
            _onlineRead = _resourceQueries.QueryAsync<ResourceContentOnlineQuery, ResourceContentOnline>(route, query, cancellationToken: _onlineStop.Token).AsTask();
            WakeOnPlatformCompletion(_onlineRead);
        }
        _onlineSection = Stack(parent, "ManagementOnlineContent", XsrUiOrientation.Vertical, 10);
        RenderOnlineContent();
    }

    private void UpdateOnlineContent()
    {
        UpdateContentIntegrity();
        UpdateContentChangelog();
        string language = DesktopResourceText.Language(_store);
        if (_onlinePresentationLanguage is { } previous && previous != language)
        {
            ApplyContentFilter(); _contentWindowStart = -1;
            if (_contentDetail is { } selected && _management?.Contents.FirstOrDefault(page => page.PageId == _selected)?.Entries.FirstOrDefault(item => item.Name == selected.Name) is { } original)
            { _contentDetail = LinkedContent(original); BuildSections(); }
            else
            {
                UpdateContentWindow();
                if (_onlineSection.IsAssigned && _shell.Tree.IsAlive(_onlineSection)) RenderOnlineContent();
            }
        }
        _onlinePresentationLanguage = language;
        UpdateOnlineList();
        if (_onlineQuery is not { } query) return;
        if (_contentDetail is not { } item || query.InstanceDirectory != _instance || query.PageId != _selected
            || query.Name != item.Name || query.ExpectedSize != item.Size || query.ExpectedModifiedUtcTicks != item.ModifiedUtcTicks)
        { CancelOnlineContent(); return; }
        bool updated = false;
        if (_onlineRead is { IsCompleted: true } read)
        {
            _onlineRead = null; updated = true;
            if (PendingQuery.Succeeded(read)) _onlineContent = read.Result.Value;
            else _onlineError = "暂时无法获取在线信息，请稍后重试。";
            if (_onlineContent?.Project?.IconUrl is { } url && _resourceQueries?.TryResolve(ResourceCatalogContract.Icon, out var route) == true)
            {
                _onlineIconRead = _resourceQueries.QueryAsync<ResourceIconQuery, ResourceIconResult>(route, new(url), cancellationToken: _onlineStop!.Token).AsTask();
                WakeOnPlatformCompletion(_onlineIconRead);
            }
        }
        if (_onlineIconRead is { IsCompleted: true } iconRead)
        {
            _onlineIconRead = null;
            if (PendingQuery.Succeeded(iconRead)) { _onlineIcon = iconRead.Result.Value!.Image; updated = true; }
        }
        if (updated && _onlineSection.IsAssigned && _shell.Tree.IsAlive(_onlineSection)) RenderOnlineContent();
        UpdateContentIntegritySource();
    }

    private void RenderOnlineContent()
    {
        foreach (var child in _shell.Tree.Children(_onlineSection).ToArray())
        {
            _shell.Tree.Walk(child, entity => { _managementActions.Remove(entity); _contentActions.Remove(entity); return true; });
            _shell.Tree.Destroy(child);
        }
        // Rebuilding only the online section keeps the local details and scroll position intact.
        Text(_onlineSection, "在线信息", 16, Ink, 28, 600);
        if (_onlineContent?.Project is { } project)
        {
            var identity = Stack(_onlineSection, "ManagementOnlineIdentity", XsrUiOrientation.Horizontal, 12);
            if (_onlineIcon is not null) ContentImage(identity, _contentDetail! with { Icon = _onlineIcon }, 48, 48);
            var titles = Stack(identity, "ManagementOnlineTitles", XsrUiOrientation.Vertical, 2);
            _shell.Tree.GetComponent<XsrUiElement>(titles)!.Weight = 1;
            ContentName(titles, DesktopResourceText.Name(project, _store), 19, 30);
            string description = DesktopResourceText.Description(project, _store);
            if (description.Length > 0) ContentName(_onlineSection, description, 13, null, maxLines: 0, foreground: Muted);
            ManagementFactIn(_onlineSection, "来源", project.SourceLabel);
            ManagementFactIn(_onlineSection, "作者", project.Author.Length > 0 ? project.Author : "未提供", literal: project.Author.Length > 0);
            ManagementFactIn(_onlineSection, "下载次数", ResourcesPageController.FormatDownloads(project.Downloads));
            ManagementFactIn(_onlineSection, "已安装版本", _onlineContent.InstalledVersion ?? "暂不可用", literal: _onlineContent.InstalledVersion is not null);
            if (_selected is "mods" or "resourcepacks" or "shaderpacks" && _onlineContent.UpdateVersion is { } update)
            {
                var row = Stack(_onlineSection, "ManagementOnlineUpdateRow", XsrUiOrientation.Horizontal, 12);
                _shell.Tree.GetComponent<XsrUiElement>(Text(row, "可更新至 " + update.Number, 13, Ink, 32))!.Weight = 1;
                if (_managementWrite is null && _resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContent, out _) == true)
                {
                    var button = ActionButton(row, "ManagementOnlineUpdate", "更新", ManagementAction, 72);
                    RegisterContentAction(button, () => UpdateOnlinePack(_onlineQuery!, update));
                }
                else if (_managementWrite is not null) Text(row, "正在更新…", 13, Muted, 32);
            }
            var versions = _onlineContent.Versions.Take(6).ToArray();
            if (versions.Length > 0)
            {
                Text(_onlineSection, "适用于当前游戏的版本", 13, Muted, 24);
                foreach (var version in versions)
                {
                    var row = Stack(_onlineSection, "ManagementOnlineVersion", XsrUiOrientation.Horizontal, 8);
                    string symbol = version.Channel switch { "正式版" => "lucide/circle-check", "Beta" => "lucide/flask-conical", _ => "lucide/test-tube" };
                    var icon = Element(row, "ManagementOnlineVersionIcon", XsrUiSemanticRole.None, null, 18, 24);
                    Style(icon, XsrUiColor.Transparent, Muted, 0); _shell.Tree.SetComponent(icon, new XsrUiImage(symbol));
                    Text(row, version.Number + " · " + version.Channel, 13, Ink, 24);
                    if (_resourceQueries?.TryResolve(ResourceCatalogContract.Changelog, out _) == true)
                        ManagementButton(row, "更新日志", () => ReadContentChangelog(version), 88);
                    if (_selected is "mods" or "resourcepacks" or "shaderpacks" && version.File is not null && _managementWrite is null
                        && _resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContent, out _) == true)
                        ManagementButton(row, _onlineContent.InstalledFiles.Any(f => f.Source.Provider == version.Provider && f.Source.ProjectId == version.ProjectId && f.VersionId == version.Id) ? "重新安装" : "安装此版本",
                            () => UpdateOnlinePack(_onlineQuery!, version), 96);
                }
            }
            else Text(_onlineSection, "暂未找到适用于当前游戏的版本。", 13, Muted, 26);
            if (_contentChangelogRead is not null) Text(_onlineSection, "正在读取更新日志…", 13, Muted, 26);
            if (_contentChangelog is { } changelog) ContentName(_onlineSection, changelog, 13, null, maxLines: 0);
            ManagementButton(_onlineSection, "查看项目", () =>
            {
                try { _openResourceLink?.Invoke(new Uri(project.Website)); }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { _feedback.Error("无法打开项目页面。"); }
            }, 96);
        }
        string? notice = _onlineContent?.Notice ?? _onlineError;
        if (notice is not null) ContentName(_onlineSection, notice, 13, null, maxLines: 0, foreground: Muted, literal: false);
        else if (_onlineRead is not null) Text(_onlineSection, "正在识别文件并获取项目资料…", 13, Muted, 26);
        if (_onlineRead is null)
            ManagementButton(_onlineSection, "重新识别", () => { CancelOnlineContent(); BuildSections(); }, 96);
        _shell.Tree.MarkDirty(_onlineSection, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
