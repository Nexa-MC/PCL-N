using System.Globalization;
using Nexa.Services.Resources;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    private const double CatalogRowHeight = 80, CatalogRowPitch = 86;
    private const int CatalogAttachedRows = 48, CatalogOverscan = 4;
    private readonly Dictionary<string, CatalogRowView> _catalogRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _catalogRowNames = new(StringComparer.Ordinal);
    private readonly List<CatalogRowView> _attachedRows = [];
    private readonly List<ResourceProject> _projectProjection = [];
    private XsrUiEntityId _catalogTopSpacer, _catalogBottomSpacer, _catalogEmpty;
    private int _windowStart = -1, _windowEnd = -1;
    private bool _windowDirty;

    private sealed class CatalogRowView(string key, ResourceProject project, XsrUiEntityId row, XsrUiEntityId icon,
        XsrUiEntityId title, XsrUiEntityId description, XsrUiEntityId metadata)
    {
        internal string Key { get; } = key;
        internal ResourceProject Project { get; set; } = project;
        internal XsrUiEntityId Row { get; } = row;
        internal XsrUiEntityId Icon { get; } = icon;
        internal XsrUiEntityId Title { get; } = title;
        internal XsrUiEntityId Description { get; } = description;
        internal XsrUiEntityId Metadata { get; } = metadata;
        internal XsrUiEntityId Details { get; set; }
        internal XsrUiEntityId Download { get; set; }
        internal string? Language { get; set; }
        internal bool Attached { get; set; }
        internal CancellationTokenSource? Media { get; set; }
    }

    private static string CatalogProjectKey(ResourceProject project) => project.Sources.Count > 0
        ? ((int)project.Sources[0].Provider).ToString(CultureInfo.InvariantCulture) + ":" + project.Sources[0].ProjectId
        : "unqualified:" + project.Id;

    private void ReconcileVirtualRows()
    {
        if (_result is null) return;
        var next = _result.Projects.Where(project => !_hideLibraries || project.Kind != ResourceKind.Mod || !project.IsLibrary)
            .Where(project => !_hideInstalled || project.Kind != ResourceKind.Mod || _context is null
                || !project.Sources.Any(reference => _context.Installed.Any(file => file.Source == reference))).ToArray();
        bool changed = !_projectProjection.Select(CatalogProjectKey).SequenceEqual(next.Select(CatalogProjectKey), StringComparer.Ordinal);
        _projectProjection.Clear(); _projectProjection.AddRange(next);
        var retained = _result.Projects.Select(CatalogProjectKey).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _catalogRows.Values.Where(row => !retained.Contains(row.Key)).ToArray()) RemoveCatalogRow(removed);
        foreach (var project in _result.Projects)
            if (_catalogRows.TryGetValue(CatalogProjectKey(project), out var row)) UpdateCatalogRow(row, project);
        if (!_catalogTopSpacer.IsAssigned)
        {
            Clear(_entities["ResourceList"], _listActions);
            _catalogTopSpacer = Element(default, "ResourceListTopSpacer");
            _catalogBottomSpacer = Element(default, "ResourceListBottomSpacer");
            _catalogEmpty = Text(default, "当前筛选下没有资源。", 14, Muted, 54);
            _windowDirty = true;
        }
        if (changed) _windowDirty = true;
        RefreshVirtualWindow();
    }

    private void RefreshVirtualWindow()
    {
        if (_result is null || !_catalogTopSpacer.IsAssigned) return;
        if (_shell.Stage.Navigation.Current != Page) { DeactivateListMedia(); return; }
        var list = _entities["ResourceList"];
        double offset = _shell.Tree.GetComponent<XsrUiScroll>(list)!.OffsetY;
        double viewport = 600;
        if (_shell.Renderer.TryGetScrollSnapshot(list, out var snapshot)) viewport = Math.Max(1, snapshot.ViewportHeight);
        int start = Math.Clamp((int)Math.Floor(Math.Max(0, offset) / CatalogRowPitch) - CatalogOverscan, 0, _projectProjection.Count);
        int count = Math.Min(CatalogAttachedRows, (int)Math.Ceiling(viewport / CatalogRowPitch) + CatalogOverscan * 2);
        int end = Math.Min(_projectProjection.Count, start + count);
        if (_windowDirty || start != _windowStart || end != _windowEnd)
        {
            var wanted = _projectProjection.Skip(start).Take(end - start).Select(CatalogProjectKey).ToHashSet(StringComparer.Ordinal);
            foreach (var old in _attachedRows)
            {
                if (!wanted.Contains(old.Key)) { DeactivateRowMedia(old); old.Attached = false; }
                _shell.Tree.Detach(old.Row);
            }
            _attachedRows.Clear();
            _shell.Tree.Detach(_catalogTopSpacer); _shell.Tree.Detach(_catalogBottomSpacer); _shell.Tree.Detach(_catalogEmpty);
            if (_projectProjection.Count == 0)
            {
                SetCatalogText(_catalogEmpty, _listBudgetReached ? "已达到列表显示上限，请缩小搜索范围。"
                    : _result.Projects.Count == 0 ? "没有找到匹配的资源。试试其他关键词或筛选条件。" : "当前筛选下没有资源。", localize: true);
                _shell.Tree.Attach(_catalogEmpty, list);
            }
            else
            {
                if (start > 0)
                { E(_catalogTopSpacer).Height = start * CatalogRowPitch - 6; _shell.Tree.Attach(_catalogTopSpacer, list); }
                for (int index = start; index < end; index++)
                {
                    var project = _projectProjection[index]; string key = CatalogProjectKey(project);
                    if (!_catalogRows.TryGetValue(key, out var row))
                    { row = CreateCatalogRow(key, project); _catalogRows.Add(key, row); }
                    UpdateCatalogRow(row, project);
                    row.Attached = true; _shell.Tree.Attach(row.Row, list); _attachedRows.Add(row);
                }
                int remaining = _projectProjection.Count - end;
                if (remaining > 0)
                { E(_catalogBottomSpacer).Height = remaining * CatalogRowPitch - 6; _shell.Tree.Attach(_catalogBottomSpacer, list); }
            }
            _windowStart = start; _windowEnd = end; _windowDirty = false;
            _shell.Tree.MarkDirty(list, XsrUiDirtyKinds.Layout);
        }
        foreach (var row in _attachedRows) ActivateRowMedia(row);
    }

    private CatalogRowView CreateCatalogRow(string key, ResourceProject project)
    {
        string name = project.Id;
        int suffix = _catalogRows.Count;
        while (_catalogRowNames.TryGetValue(name, out var owner) && owner != key)
            name = project.Id + "." + (suffix++).ToString(CultureInfo.InvariantCulture);
        _catalogRowNames[name] = key;
        var body = Card(default, "ResourceProject." + name);
        var row = _shell.Tree.Parent(body);
        E(row).Height = CatalogRowHeight; E(body).Height = CatalogRowHeight;
        var icon = Element(body, "ResourceProjectIcon"); E(icon).Width = 48; E(icon).Height = 48; E(icon).VerticalAlignment = XsrUiAlignment.Center;
        Style(icon, Tint, Muted, 12);
        _shell.Tree.SetComponent(icon, new XsrUiImage(project.Kind switch
        { ResourceKind.Mod => "lucide/blocks", ResourceKind.Shader => "nexa/content-shader", _ => "nexa/content-package" }));
        var copy = Stack(body, "ResourceProjectCopy"); E(copy).Weight = 1;
        var title = LiteralText(copy, "", 15, Ink, 22, 600);
        var description = LiteralText(copy, "", 12, Muted, 20);
        var metadata = LiteralText(copy, "", 11, Muted, 18);
        var view = new CatalogRowView(key, project, row, icon, title, description, metadata);
        view.Details = IconButton(body, "ResourceDetails." + name, "详情", "lucide/info", () =>
        {
            if (!view.Attached || _shell.Stage.Navigation.Current != Page) return;
            _detailProject = view.Project; _detailPage = 0;
            DeactivateListMedia(); _shell.Stage.Navigation.Push(DetailPage); ReadDetail(view.Project.Id);
        });
        view.Download = IconButton(body, "ResourceQuickDownload." + name, "下载", "lucide/download", () =>
        {
            if (view.Attached && _shell.Stage.Navigation.Current == Page) _ = DownloadProjectAsync(view.Project);
        });
        _listActions.Add(view.Details); _listActions.Add(view.Download);
        return view;
    }

    private void UpdateCatalogRow(CatalogRowView row, ResourceProject project)
    {
        string language = DesktopResourceText.Language(_store);
        if (row.Language == language && CatalogFactsEqual(row.Project, project)) return;
        DeactivateRowMedia(row); row.Project = project; row.Language = language;
        SetCatalogText(row.Title, ProjectTitle(DesktopResourceText.Name(project, _store)));
        SetCatalogText(row.Description, DesktopResourceText.Description(project, _store));
        string count = FormatDownloads(Math.Max(0, ResourceCaptions.DownloadCount(_functionPatches.Runtime, _functionPatches.DownloadCount, project.Downloads)));
        SetCatalogText(row.Metadata, project.SourceLabel + "  ·  " + project.Author + "  ·  " + count + " " + _shell.Renderer.LocalizeText("次下载"));
    }

    private static bool CatalogFactsEqual(ResourceProject left, ResourceProject right) =>
        left.Sources.SequenceEqual(right.Sources) && (left with { Sources = right.Sources }) == right;

    private void SetCatalogText(XsrUiEntityId entity, string text, bool localize = false)
    {
        var old = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        _shell.Tree.SetComponent(entity, new XsrUiText(text) { Localize = localize, MaxLines = old.MaxLines, TrimOverflow = true });
        _shell.Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.Text, text) { Localize = localize });
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private void ActivateRowMedia(CatalogRowView row)
    {
        if (_listMediaPaused || row.Media is { IsCancellationRequested: false }) return;
        DeactivateRowMedia(row);
        row.Media = CancellationTokenSource.CreateLinkedTokenSource(_iconStop.Token, _translationStop.Token);
        TrackIcon(row.Icon, row.Project, Page, row.Media.Token);
        Translate(row.Description, row.Project, row.Media.Token);
    }

    private void DeactivateRowMedia(CatalogRowView row)
    {
        row.Media?.Cancel(); row.Media?.Dispose(); row.Media = null;
        _iconDescriptors.RemoveAll(item => item.Entity == row.Icon);
        _icons.RemoveAll(item => item.Entity == row.Icon);
        _translations.RemoveAll(item => item.Entity == row.Description);
        if (_shell.Tree.IsAlive(row.Icon) && _shell.Tree.GetComponent<XsrUiImage>(row.Icon) is { Raster: not null } image)
        { image.Raster = null; _shell.Tree.MarkDirty(row.Icon, XsrUiDirtyKinds.Paint); }
    }

    private void DeactivateListMedia()
    {
        foreach (var row in _attachedRows) DeactivateRowMedia(row);
    }

    private void RemoveCatalogRow(CatalogRowView row)
    {
        DeactivateRowMedia(row); row.Attached = false;
        _attachedRows.Remove(row); _catalogRows.Remove(row.Key);
        foreach (var name in _catalogRowNames.Where(pair => pair.Value == row.Key).Select(pair => pair.Key).ToArray()) _catalogRowNames.Remove(name);
        foreach (var action in new[] { row.Details, row.Download })
        {
            _actions.Remove(action); _listActions.Remove(action);
            foreach (var name in _entities.Where(item => item.Value == action).Select(item => item.Key).ToArray()) _entities.Remove(name);
        }
        if (_shell.Tree.IsAlive(row.Row)) _shell.Tree.Destroy(row.Row);
        _windowDirty = true;
    }

    private void DestroyVirtualRows()
    {
        foreach (var row in _catalogRows.Values.ToArray()) RemoveCatalogRow(row);
        foreach (var spacer in new[] { _catalogTopSpacer, _catalogBottomSpacer, _catalogEmpty })
            if (spacer.IsAssigned && _shell.Tree.IsAlive(spacer)) _shell.Tree.Destroy(spacer);
        _catalogTopSpacer = _catalogBottomSpacer = _catalogEmpty = default;
        _catalogRows.Clear(); _catalogRowNames.Clear(); _attachedRows.Clear(); _projectProjection.Clear();
        _windowStart = _windowEnd = -1; _windowDirty = true;
    }
}
