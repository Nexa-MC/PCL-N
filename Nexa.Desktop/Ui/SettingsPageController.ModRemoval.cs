using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private Task<XsrResult<InstanceModRemovalPreview>>? _modRemovalRead;
    private InstanceContentRemoveCommand? _modRemovalPrimary;
    private CancellationTokenSource? _modRemovalStop;

    private void CancelModRemovalPreview()
    {
        _modRemovalStop?.Cancel(); _modRemovalStop?.Dispose(); _modRemovalStop = null;
        _modRemovalRead = null; _modRemovalPrimary = null;
    }
    private void StartModRemovalPreview(InstanceContentRemoveCommand command, XsrQueryId route)
    {
        CancelModRemovalPreview(); _modRemovalPrimary = command; _modRemovalStop = new();
        _modRemovalRead = _queries.QueryAsync<InstanceModRemovalQuery, InstanceModRemovalPreview>(route, new(command), cancellationToken: _modRemovalStop.Token).AsTask();
        WakeOnPlatformCompletion(_modRemovalRead);
    }
    private void UpdateModRemovalPreview()
    {
        if (_modRemovalPrimary is not { } primary) return;
        if (_instance != primary.InstanceDirectory || _selected != "mods") { CancelModRemovalPreview(); return; }
        if (_modRemovalRead is not { IsCompleted: true } read) return;
        _modRemovalRead = null; _modRemovalPrimary = null; _modRemovalStop?.Dispose(); _modRemovalStop = null;
        if (!PendingQuery.Succeeded(read) || !_commands.TryResolve(InstanceManagementContract.RemoveMod, out var route))
        { _feedback.Error("无法检查依赖关系，请刷新后重试。"); return; }
        var preview = read.Result.Value!;
        string Format(string source, object argument) => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            _shell.Renderer.LocalizeText(source), argument);
        string message = Format("将“{0}”移至已移除内容，可随时还原。", RecoveryExplanation.Display(primary.Name));
        if (preview.RequiredBy.Count > 0) message += _shell.Renderer.LocalizeText("\n以下模组依赖它（包括间接依赖）：") + string.Join("、", preview.RequiredBy.Take(12).Select(RecoveryExplanation.Display))
            + (preview.RequiredBy.Count > 12 ? Format("；还有 {0} 个。", preview.RequiredBy.Count - 12) : "。");
        if (preview.Orphans.Count > 0) message += _shell.Renderer.LocalizeText("\n以下前置已无其他模组需要，可选择一并移除：\n") + string.Join("\n", preview.Orphans.Take(12).Select(item => RecoveryExplanation.Display(item.Name)))
            + (preview.Orphans.Count > 12 ? Format("\n还有 {0} 个。", preview.Orphans.Count - 12) : "");
        if (preview.Notice is not null) message += "\n" + _shell.Renderer.LocalizeText(preview.Notice);
        void Remove(bool withOrphans)
        {
            if (_managementWrite is not null || _instance != primary.InstanceDirectory || _selected != "mods") return;
            _managementWriteInstance = primary.InstanceDirectory;
            _managementWrite = _commands.Dispatch(route, new InstanceModRemovalCommand(primary, withOrphans ? preview.Orphans : [])).Completion;
            WakeOnPlatformCompletion(_managementWrite);
        }
        _feedback.ShowDialog("content.remove-mod", "移除模组", message, preview.Orphans.Count > 0 ? "仅移除此模组" : "移除", "取消",
            accepted => { if (accepted) Remove(false); }, preview.Orphans.Count > 0 ? "连同前置移除" : null, () => Remove(true), localizeMessage: false);
    }
}
