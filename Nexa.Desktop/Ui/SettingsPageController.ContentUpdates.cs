using Nexa.Services.Minecraft.Management;
using Nexa.Services.Resources;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private readonly HashSet<ResourceContentOnlineQuery> _selectedContentUpdates = [];
    private Task<Nexa.Xsr.XsrResult<ResourceChangelog>>? _contentChangelogRead;
    private string? _contentChangelog;

    private void ReadContentChangelog(ResourceVersion version)
    {
        if (_resourceQueries?.TryResolve(ResourceCatalogContract.Changelog, out var route) != true || _contentChangelogRead is not null) return;
        _contentChangelog = null;
        _contentChangelogRead = _resourceQueries.QueryAsync<ResourceChangelogQuery, ResourceChangelog>(route,
            new(new(version.Provider, version.ProjectId), version.Id, _onlineQuery?.MirrorFirst ?? true), cancellationToken: _onlineStop?.Token ?? default).AsTask();
        WakeOnPlatformCompletion(_contentChangelogRead);
    }

    private void UpdateContentChangelog()
    {
        if (_contentChangelogRead is not { IsCompleted: true } completed) return;
        _contentChangelogRead = null;
        _contentChangelog = PendingQuery.Succeeded(completed) ? completed.Result.Value!.Text : "暂时无法获取更新日志。";
        if (string.IsNullOrWhiteSpace(_contentChangelog)) _contentChangelog = "此版本未提供更新日志。";
        if (_onlineSection.IsAssigned && _shell.Tree.IsAlive(_onlineSection)) RenderOnlineContent();
    }

    private void BuildContentUpdateActions(XsrUiEntityId parent)
    {
        if (_resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContentBatch, out _) != true) return;
        var row = Stack(parent, "ContentUpdateActions", XsrUiOrientation.Horizontal, 10);
        ManagementButton(row, "更新已选择", () => DispatchContentUpdates(selectedOnly: true), 106);
        ManagementButton(row, "更新全部已识别", () => DispatchContentUpdates(selectedOnly: false), 136);
        Text(row, "批量更新保留联合撤回记录", 12, Muted, 32);
    }

    private void DispatchContentUpdates(bool selectedOnly)
    {
        if (_instance is null || _managementWrite is not null || _resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContentBatch, out var route) != true) return;
        var updates = _onlineList.Where(pair => pair.Key.InstanceDirectory == _instance && pair.Key.PageId == _selected
            && pair.Value.UpdateVersion is not null && (!selectedOnly || _selectedContentUpdates.Contains(pair.Key)))
            .Select(pair => new ResourceContentUpdateCommand(pair.Key, new(pair.Value.UpdateVersion!.Provider, pair.Value.UpdateVersion.ProjectId), pair.Value.UpdateVersion.Id)).ToArray();
        if (updates.Length == 0) { _feedback.Error("没有已识别且可更新的选中内容。"); return; }
        if (updates.Length > 100) { _feedback.Error("一次最多更新 100 项，请分批选择。"); return; }
        _managementWriteInstance = _instance;
        _managementWrite = _resourceCommands.Dispatch(route, new ResourceContentUpdateBatchCommand(updates)).Completion;
        CancelOnlineList(); _selectedContentUpdates.Clear(); WakeOnPlatformCompletion(_managementWrite);
        BuildSections(true);
    }

    private void BuildContentUpdateSelection(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (OnlineFile(item) is not { } query || _resourceCommands?.TryResolve(ResourceCatalogContract.UpdateContentBatch, out _) != true) return;
        var choice = ToggleControl(parent, "ContentUpdateSelect." + item.Name, "选择", _selectedContentUpdates.Contains(query), ManagementAction, checkBox: true, width: 68);
        RegisterContentAction(choice, () =>
        {
            if (!_selectedContentUpdates.Remove(query)) _selectedContentUpdates.Add(query);
            if (_shell.Tree.IsAlive(choice)) _shell.Tree.GetComponent<XsrUiToggle>(choice)!.IsChecked = _selectedContentUpdates.Contains(query);
        });
    }

    private void BuildContentUpdateHistory(InstanceManagementSnapshot snapshot)
    {
        if (snapshot.ContentUpdates.Count == 0) return;
        Text(_sections, "内容更新事务", 16, Ink, 30, 600);
        foreach (var item in snapshot.ContentUpdates.Take(20))
        {
            var row = Stack(_sections, "ContentUpdateRecord", XsrUiOrientation.Horizontal, 12);
            var label = Text(row, item.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.CurrentCulture) + " · "
                + string.Format(System.Globalization.CultureInfo.CurrentCulture, _shell.Renderer.LocalizeText("{0} 项"), item.Items) + " · "
                + _shell.Renderer.LocalizeText(item.Phase == "rolled-back" ? "已撤回" : item.Phase == "committed" ? "已完成" : "需要恢复"), 13, Ink, 38);
            _shell.Tree.GetComponent<XsrUiElement>(label)!.Weight = 1;
            if (item.Phase == "rolled-back") continue;
            ManagementButton(row, "联合撤回", () =>
            {
                if (_managementWrite is not null || _instance != snapshot.InstanceDirectory || !_commands.TryResolve(InstanceManagementContract.RollbackContentUpdate, out var route)) return;
                _managementWriteInstance = snapshot.InstanceDirectory;
                _managementWrite = _commands.Dispatch(route, new InstanceContentUpdateRollbackCommand(snapshot.InstanceDirectory, item.Id)).Completion;
                WakeOnPlatformCompletion(_managementWrite);
            }, 90);
        }
    }
}
