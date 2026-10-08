using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private XsrSemanticId _boundDestination = XsrSemanticId.Parse("navigation.launch");

    private void OpenBoundDestination(XsrSemanticId command)
    {
        XsrUiEntityId page = command.Value switch
        { "ui.navigation.settings" => SettingsPage, "ui.navigation.community" => ResourcesPage, _ => default };
        if (!page.IsAssigned || !_shell.Tree.IsAlive(page))
        {
            _ = _shell.Select(_boundDestination);
            _feedback.Warn(command.Value switch
            {
                "ui.navigation.settings" => "设置页面未绑定。",
                "ui.navigation.community" => "资源页面未绑定。",
                _ => "无法导航：未识别的页面入口。"
            });
            return;
        }
        ClearSubpageHistory();
        _boundDestination = command.Value == "ui.navigation.settings" ? XsrSemanticId.Parse("navigation.settings") : XsrSemanticId.Parse("navigation.community");
        _ = _shell.Select(_boundDestination);
        if (_shell.Stage.Navigation.Current != page) _shell.Stage.Navigation.Replace(page);
    }

    private void OpenBoundSubpage(XsrUiEntityId page, XsrUiEntityId source, string unavailable)
    {
        if (!page.IsAssigned || !_shell.Tree.IsAlive(page)) { _feedback.Warn(unavailable); return; }
        OpenSubpage(page, source);
    }

    private void BindBorrowedSubpage(ref XsrUiEntityId binding, XsrUiEntityId page)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!page.IsAssigned || !_shell.Tree.IsAlive(page) || _shell.Tree.GetComponent<XsrUiSemantic>(page)?.Role != XsrUiSemanticRole.Page)
            throw new ArgumentException("The bound navigation page must be a live Page in this controller's tree.", nameof(page));
        if (binding == page) return;
        bool current = binding.IsAssigned && _shell.Stage.Navigation.Current == binding;
        binding = page;
        if (current) { _shell.Stage.Navigation.Replace(page); UpdateTitleBar(); }
    }
}
