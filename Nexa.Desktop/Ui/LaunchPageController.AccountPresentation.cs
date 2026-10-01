
using Nexa.Pxml;
using Nexa.Services.Accounts;






using Nexa.UI.Next;
using Nexa.Xsr;

using Nexa.Xsr.State;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{

    private void RefreshAccountPresentation()
    {
        XsrCollectionSnapshot<LaunchProfileView> roster = _store.ReadCollection<LaunchProfileView>(
            _accountProfilesId);
        int selected = SelectedAccountIndex;
        bool refreshSkins = roster.Revision != _accountRosterRevision || selected != _presentedAccountIndex;
        if (roster.Revision != _accountRosterRevision)
        {
            BuildAccountRows(roster.Items);
            _accountRosterRevision = roster.Revision;
            _presentedAccountIndex = -2;
            _skinRevision = -1;
        }

        if (selected != _presentedAccountIndex)
        {
            StyleAccountRows();
            _presentedAccountIndex = selected;
            LaunchProfileView profile = roster.Items.FirstOrDefault(item => item.Index == selected);
            string avatar = LaunchProfilePresentation.Avatar(profile.Uuid ?? string.Empty);
            XsrUiEntityId image = _pageEntities["AccountAvatar"];
            _shell.Tree.GetComponent<XsrUiImage>(image)!.Source = avatar;
            _shell.Tree.MarkDirty(image, XsrUiDirtyKinds.Paint);
            _skinRevision = -1;
        }

        if (refreshSkins && _accountCommands?.TryResolve(AccountSkinContract.RefreshRoute, out XsrCommandId skinRoute) == true)
            _ = _accountCommands.Dispatch(skinRoute, new AccountRefreshSkinsCommand(), cancellationToken: _lifetimeCancellation.Token).Completion;
        RefreshSkinPresentation(roster.Items, selected);

        bool hasSelection = roster.Items.Any(profile => profile.Index == selected);
        bool picker = !hasSelection || _store.ReadAppliedValue(_store.Resolve(LaunchPageState.AccountPickerKey)) is true;
        bool onboarding = _store.ReadAppliedValue(_store.Resolve(AccountFormState.Open)) is true;
        string motionKey = onboarding
            ? "form:" + ReadCell(AccountFormState.Mode) + ":" + _store.Read<AccountLoginSnapshot>(_store.Resolve(AccountOnboardingState.Login)).Value?.Phase
            : picker ? "roster" : "selected:" + selected;
        Publish(LaunchPageState.AccountTransitionKey, motionKey);
        if (_accountMotionKey != motionKey)
        {
            XsrUiTransition.ConfigureIndependent(_shell.Tree, _pageEntities["AccountBody"], "account:" + motionKey);
            _accountMotionKey = motionKey;
        }
        Publish(LaunchPageState.AccountRosterVisibleKey, picker && !onboarding);
        Publish(LaunchPageState.AccountSelectedVisibleKey, !picker && !onboarding);
        Publish(LaunchPageState.AccountCanReturnKey, hasSelection);
        Publish(LaunchPageState.AccountTitleKey, onboarding
            ? _store.ReadAppliedValue(_store.Resolve(AccountFormState.Key("title"))) as string ?? "添加账户"
            : picker && hasSelection ? "切换档案" : "账户");
        Publish(LaunchPageState.AccountBackVisibleKey, onboarding || (picker && hasSelection));
        Publish(LaunchPageState.AccountAddVisibleKey, !onboarding);
        Publish(LaunchPageState.AccountHintVisibleKey, roster.Count == 0 || roster.Availability == XsrStateAvailability.Unavailable);
        Publish(LaunchPageState.AccountHintKey, roster.Availability == XsrStateAvailability.Unavailable
            ? "账户安全存储不可用，原档案已保留。\n请解锁系统密钥库并重启后重试。" : roster.Count == 0
            ? "还没有账户档案。\n点击上方＋添加，或导入旧档案。"
            : string.Empty);
        if (_presentedAccountPicker is { } previous && previous != picker
            && _shell.Stage.Navigation.Current == _launchPage)
        {
            XsrUiEntityId focus = picker
                ? _accountRowEntities.GetValueOrDefault(selected, _accountRowEntities.Values.FirstOrDefault())
                : _pageEntities["AccountSwitch"];
            if (focus.IsAssigned) _shell.Renderer.Focus(focus, _accountKeyboardFocus);
        }
        _presentedAccountPicker = picker;
        PublishProfileFacts();
        UpdateLaunchButton();
    }


    private void RefreshSkinPresentation(IReadOnlyList<LaunchProfileView> profiles, int selected)
    {
        var skins = _store.ReadCollection<AccountSkinSnapshot>(_store.Resolve(AccountSkinContract.SkinsKey));
        if (_skinRevision == skins.Revision) return;
        _skinRevision = skins.Revision;
        Dictionary<string, AccountSkinSnapshot> images = skins.Items.ToDictionary(item => item.ProfileKey);
        foreach (LaunchProfileView profile in profiles)
        {
            XsrUiRasterImage? raster = images.TryGetValue(AccountSkinContract.ProfileKey(profile), out AccountSkinSnapshot? skin)
                && skin.Image is { } png ? LaunchProfilePresentation.Head(png) : null;
            if (_accountRowEntities.TryGetValue(profile.Index, out XsrUiEntityId row))
                _shell.Tree.Walk(row, entity =>
                {
                    if (_shell.Tree.Name(entity).StartsWith("ProfileAvatar:", StringComparison.Ordinal)) SetRaster(entity, raster);
                    return true;
                });
            if (profile.Index == selected) SetRaster(_pageEntities["AccountAvatar"], raster);
        }
    }


    private void SetRaster(XsrUiEntityId entity, XsrUiRasterImage? raster)
    {
        _shell.Tree.GetComponent<XsrUiImage>(entity)!.Raster = raster;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }


    /// <summary>
    /// Rebuilds the account card's profile list: one clickable row per roster profile, the
    /// selected one highlighted. Rows emit <see cref="AccountSelectCommand"/> with themselves
    /// as the intent source, so the renderer keeps owning invocation and correlation.
    /// </summary>
    private void BuildAccountRows(IReadOnlyList<LaunchProfileView> profiles)
    {
        if (!_pageEntities.TryGetValue("AccountRows", out XsrUiEntityId rowsHost))
        {
            return;
        }

        XsrUiTree tree = _shell.Tree;
        foreach (XsrUiEntityId row in _accountRowEntities.Values)
        {
            tree.Destroy(row);
        }

        _accountRowEntities.Clear();
        _accountRowIndexes.Clear();

        foreach (LaunchProfileView profile in profiles)
        {
            XsrUiEntityId row = PxmlUiLoader.Load(new PxmlHostIr(ProjectProfileNode(_accountRowTemplate.Root, profile)),
                tree, _store, rowsHost);
            tree.SetComponent(row, new XsrUiSelection());
            tree.Walk(row, entity =>
            {
                string key = tree.Name(entity);
                if (key.StartsWith("ProfileName:", StringComparison.Ordinal)
                    || key.StartsWith("ProfileDetail:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(profile.Info)) DesktopLiteralText.Preserve(tree, entity);
                return true;
            });
            _accountRowEntities[profile.Index] = row;
            _accountRowIndexes[row] = profile.Index;
        }

        tree.MarkDirty(rowsHost, XsrUiDirtyKinds.Structure);
        StyleAccountRows();
    }


    private static PxmlIrNode ProjectProfileNode(PxmlIrNode node, LaunchProfileView profile) => node with
    {
        Key = node.Key == "AccountRow" ? $"account-row:{profile.Index}" : $"{node.Key}:{profile.Index}",
        Label = node.Key == "AccountRow" ? $"选择 {profile.Username}，{ProfileKind(profile.Kind)}"
            : node.Key == "ProfileDelete" ? $"删除档案 {profile.Username}" : node.Label,
        Content = node.Key switch
        {
            "ProfileName" => profile.Username,
            "ProfileDetail" => LaunchProfilePresentation.Description(profile),
            _ => node.Content,
        },
        ImageSource = node.Key == "ProfileAvatar" ? LaunchProfilePresentation.Avatar(profile.Uuid) : node.ImageSource,
        Children = [.. node.Children.Select(child => ProjectProfileNode(child, profile))],
    };


    private static string ProfileKind(LaunchProfileKind kind) => kind switch
    {
        LaunchProfileKind.Microsoft => "Microsoft 账户",
        LaunchProfileKind.ThirdParty => "第三方账户",
        LaunchProfileKind.Offline => "离线账户",
        LaunchProfileKind.LittleSkin => "LittleSkin 账户",
        LaunchProfileKind.NCloud => "NCloud 账户",
        _ => "账户档案",
    };


    private void StyleAccountRows()
    {
        foreach ((int index, XsrUiEntityId row) in _accountRowEntities)
        {
            bool selected = index == SelectedAccountIndex;
            if (_shell.Tree.GetComponent<XsrUiSelection>(row) is { } selection)
            {
                selection.IsSelected = selected;
            }

            ApplyVisual(
                row,
                selected ? BadgeBackground : ProfileSurface,
                PrimaryText,
                cornerRadius: XsrUiCornerRadii.Inset, hover: PickerBackground);
            _shell.Tree.Walk(row, entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.StartsWith("ProfileDelete:", StringComparison.Ordinal))
                {
                    ApplyVisual(entity, XsrUiColor.Transparent, ProfileSecondaryText, XsrUiCornerRadii.Pill(28),
                        hover: new XsrUiColor(255, 224, 224));
                    return true;
                }
                bool detail = key.StartsWith("ProfileDetail:", StringComparison.Ordinal);
                StyleText(entity, detail ? ProfileSecondaryText : selected ? BadgeText : PrimaryText,
                    fontSize: detail ? 12 : 14, weight: detail ? 400 : 600);
                return true;
            });
        }
    }


    private void ShowPlaceholder(bool settings = false)
    {
        ClearSubpageHistory();
        if (settings && SettingsPage.IsAssigned)
        {
            _shell.Stage.Navigation.Replace(SettingsPage);
            return;
        }
        if (!_shell.Stage.Navigation.Current.Equals(_placeholderPage))
        {
            _shell.Stage.Navigation.Replace(_placeholderPage);
        }
    }


    private void NavigateToDownload()
    {
        _ = _shell.Select(DownloadNavigationId);
        ShowInstallRoot();
        _feedback.Info("请在安装页选择或下载游戏版本。");
    }


    private void PublishProfileFacts()
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        int index = SelectedAccountIndex;
        if (_pageEntities.TryGetValue("AccountName", out var accountName))
        {
            bool placeholder = index < 0 || index >= profiles.Count;
            if (_shell.Tree.GetComponent<XsrUiText>(accountName) is { } nameText) nameText.Localize = placeholder;
            if (_shell.Tree.GetComponent<XsrUiSemantic>(accountName) is { } nameLabel) nameLabel.Localize = placeholder;
        }
        if (index >= 0 && index < profiles.Count)
        {
            if (_pageEntities.TryGetValue("AccountKind", out var accountKind)
                && _shell.Tree.GetComponent<XsrUiText>(accountKind) is { } kindText)
                kindText.Localize = profiles[index].Kind == LaunchProfileKind.ThirdParty || string.IsNullOrWhiteSpace(profiles[index].Info);
            Publish(LaunchPageState.ProfileNameKey, profiles[index].Username);
            Publish(LaunchPageState.ProfileKindKey, LaunchProfilePresentation.Description(profiles[index]));
        }
        else
        {
            Publish(LaunchPageState.ProfileNameKey, NoAccountName);
            Publish(LaunchPageState.ProfileKindKey, string.Empty);
        }
    }

}
