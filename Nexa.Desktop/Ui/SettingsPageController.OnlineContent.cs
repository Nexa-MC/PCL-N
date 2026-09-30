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

    internal void ConfigureOnlineContent(XsrQueryRouter queries, Action<Uri> open)
    { _resourceQueries = queries; _openResourceLink = open; }

    private void CancelOnlineContent()
    {
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
        if (_onlineQuery is not { } query) return;
        if (_contentDetail is not { } item || query.InstanceDirectory != _instance || query.PageId != _selected
            || query.Name != item.Name || query.ExpectedSize != item.Size || query.ExpectedModifiedUtcTicks != item.ModifiedUtcTicks)
        { CancelOnlineContent(); return; }
        bool updated = false;
        if (_onlineRead is { IsCompleted: true } read)
        {
            _onlineRead = null; updated = true;
            if (read.IsCompletedSuccessfully && read.Result.IsSuccess) _onlineContent = read.Result.Value;
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
            if (iconRead.IsCompletedSuccessfully && iconRead.Result.IsSuccess) { _onlineIcon = iconRead.Result.Value!.Image; updated = true; }
        }
        if (updated && _onlineSection.IsAssigned && _shell.Tree.IsAlive(_onlineSection)) RenderOnlineContent();
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
            ContentName(titles, project.DisplayName, 19, 30);
            if (project.DisplayDescription.Length > 0) ContentName(_onlineSection, project.DisplayDescription, 13, null, maxLines: 0, foreground: Muted);
            ManagementFactIn(_onlineSection, "来源", project.SourceLabel);
            ManagementFactIn(_onlineSection, "作者", project.Author.Length > 0 ? project.Author : "未提供", literal: project.Author.Length > 0);
            ManagementFactIn(_onlineSection, "下载次数", ResourcesPageController.FormatDownloads(project.Downloads));
            ManagementFactIn(_onlineSection, "已安装版本", _onlineContent.InstalledVersion ?? "暂不可用", literal: _onlineContent.InstalledVersion is not null);
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
                }
            }
            else Text(_onlineSection, "暂未找到适用于当前游戏的版本。", 13, Muted, 26);
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
