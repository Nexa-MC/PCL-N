using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class ResourcesPageController
{
    private static readonly string[] SourcePolicyOptions = ["ResourceNetwork.0", "ResourceNetwork.1"];
    private XsrUiEntityId _sourcePolicyNote;
    private long _sourcePolicyRevision = -1;
    private bool _sourcePolicyForced;
    private Task<XsrResult<ResourceNetworkPolicySnapshot>>? _sourcePolicyReading;

    private void InitializeSourcePolicyNotice()
    {
        _sourcePolicyNote = Text(_entities["ResourcesLayout"], "资源站来源顺序由全局设置控制。", 11, Muted, 28);
        E(_sourcePolicyNote).IsVisible = false;
    }

    private void SelectSourceFromPage(int index)
    {
        if (_sourcePolicyForced) return;
        _filter = _filter with { MirrorFirst = index == 0 }; Search(0);
    }

    private void UpdateSourcePolicyNotice()
    {
        if (_disposed || _shell.Stage.Navigation.Current != Page) return;
        long revision = _store.TryResolve(SettingsPolicyContract.RevisionKey, out var key) ? _store.Read<long>(key).Value : 0;
        if (_sourcePolicyReading is null && _sourcePolicyRevision != revision
            && _queries.TryResolve(ResourceCatalogContract.NetworkPolicy, out var route))
        {
            _sourcePolicyRevision = revision;
            _sourcePolicyReading = _queries.QueryAsync<ResourceNetworkPolicyQuery, ResourceNetworkPolicySnapshot>(route, new()).AsTask();
            Wake(_sourcePolicyReading);
        }
        if (_sourcePolicyReading is not { IsCompleted: true } read) return;
        _sourcePolicyReading = null;
        if (!read.IsCompletedSuccessfully || !read.Result.IsSuccess) return;
        string priority = read.Result.Value!.Priority;
        _sourcePolicyForced = priority is "official-first" or "mirrors-first";
        string source = priority == "official-first" ? "资源站使用全局官方优先。" : "资源站使用全局镜像优先。";
        E(_sourcePolicyNote).IsVisible = _sourcePolicyForced;
        _shell.Tree.GetComponent<XsrUiText>(_sourcePolicyNote)!.Content = source;
        _shell.Tree.GetComponent<XsrUiSemantic>(_sourcePolicyNote)!.Label = source;
        int selected = (_sourcePolicyForced ? priority == "mirrors-first" : _filter.MirrorFirst) ? 0 : 1;
        for (int index = 0; index < SourcePolicyOptions.Length; index++)
        {
            var option = _entities[SourcePolicyOptions[index]];
            _shell.Tree.GetComponent<XsrUiInput>(option)!.Enabled = !_sourcePolicyForced;
            _shell.Tree.GetComponent<XsrUiSelection>(option)!.IsSelected = index == selected;
            if (index == selected) _shell.Tree.GetComponent<XsrUiSegmentedTrack>(_entities["ResourceNetwork"])!.Selected = option;
            _shell.Tree.MarkDirty(option, XsrUiDirtyKinds.Paint);
        }
        _shell.Tree.MarkDirty(_sourcePolicyNote, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }
}
