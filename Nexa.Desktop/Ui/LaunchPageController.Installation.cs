
using Nexa.Pxml;




using Nexa.Services.Minecraft.Install;


using Nexa.UI.Next;
using Nexa.Xsr;



namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{

    private void SelectInstallVersion(string version, string key)
    {
        if (_editingInstall) return;
        _selectedInstallVersion = version;
        ChooseInstallGame(version);
        _ = _shell.Renderer.SetTextInputValue(_javaInstallEntities["JavaInstallVersionInput"], version);
        StyleInstallChoices(
            _javaInstallEntities,
            ["JavaVersion1211", "JavaVersion1206", "JavaVersion1201"],
            key,
            InstallJavaTint,
            InstallJavaAccent);
    }


    private void SelectInstallLoader(string loader, string key)
    {
        _selectedInstallLoader = loader;
        if (loader == "原版 Minecraft") _selectedInstallBuilds.Clear();
        _selectedInstallAddons.Clear();
        foreach (InstallLoader addon in _selectedInstallBuilds.Keys.Where(kind => QueryInstallEligibility()?.Loaders.Any(item => item.Loader == kind && item.IsAddon) == true))
            _selectedInstallAddons.Add(addon == InstallLoader.FabricApi ? "Fabric API" : addon == InstallLoader.Qsl ? "QSL" : "OptiFabric");
        StyleInstallChoices(
            _javaInstallEntities,
            JavaInstallLoaderKeys,
            key,
            InstallJavaTint,
            InstallJavaAccent);
        UpdateInstallAddonStyle("Fabric API", "JavaFabricApiSelect", selected: false);
        UpdateInstallAddonStyle("QSL", "JavaQslSelect", selected: false);
        JavaInstallSubpage? selectedPage = JavaInstallSubpages.FirstOrDefault(candidate => candidate.Loader == loader);
        UpdateJavaInstallSubpageVisibility(selectedPage?.PageKey ?? "JavaMinecraftPage");
    }


    private void ToggleInstallAddon(string addon, string key)
    {
        string requiredLoader = addon == "Fabric API" ? "Fabric" : "Quilt";
        if (_selectedInstallLoader != requiredLoader)
        {
            return;
        }

        bool selected = _selectedInstallAddons.Add(addon);
        if (!selected)
        {
            _selectedInstallAddons.Remove(addon);
        }

        UpdateInstallAddonStyle(addon, key, selected);
    }


    private void NotifyInstallUnavailable()
    {
        if (_editingInstall && _installEdit is null) { _feedback.Warn("正在读取版本信息。"); return; }
        if (!_installGameChosen) { _feedback.Warn("请先选择 Minecraft 版本。"); return; }
        if (QueryInstallEligibility()?.CommitError is { } conflict) { _feedback.Warn(conflict); return; }
        string requested = _shell.Tree.GetComponent<XsrUiTextInput>(_javaInstallEntities["JavaInstallVersionInput"])
            ?.ReadDraft().Trim() ?? string.Empty;
        if (_editingInstall && requested.Length == 0) { _feedback.Warn("请输入版本名称。"); return; }
        if (_editingInstall && _editPlan?.Kind == MinecraftInstallEditKind.Unchanged && requested == _installEdit?.InstanceId) return;
        string version = requested.Length == 0 ? _selectedInstallVersion : requested;
        string selection = _selectedInstallBuilds.Count == 0 ? _selectedInstallLoader : string.Join(" + ", _selectedInstallBuilds.Select(pair => pair.Key + " " + pair.Value));
        // The selection is real: dispatch the install run and follow it in the task center.
        if (!BeginInstallRun(version)) { _feedback.Warn($"无法开始安装 Java 版 {version}（{selection}）。"); }
    }


    /// <summary>
    /// Dispatches the real install pipeline with the current selection. The primary loader is
    /// the first selected build the eligibility query does not classify as an addon; every
    /// other selected build rides along as an addon jar. Returns false when the install route
    /// is unavailable (tests without the runtime) or the selection is empty.
    /// </summary>
    private bool BeginInstallRun(string version)
    {
        if (_installRunCommands is null
            || !_installRunCommands.TryResolve(MinecraftInstallRoutes.Run, out XsrCommandId route))
        {
            return false;
        }

        InstallEligibilityResult? eligibility = QueryInstallEligibility();
        (InstallLoader Kind, string Build)? primary = null;
        List<MinecraftInstallAddon> addons = [];
        foreach (KeyValuePair<InstallLoader, string> build in _selectedInstallBuilds)
        {
            bool isAddon = eligibility?.Loaders.Any(
                loader => loader.Loader == build.Key && loader.IsAddon) == true;
            if (isAddon)
            {
                var catalog = _store.ReadAppliedValue(_installCatalogId) as InstallCatalogState;
                var downloads = catalog?.Catalogs.FirstOrDefault(item => item.Loader == build.Key && item.GameVersion == _selectedInstallVersion)
                    ?.Versions.FirstOrDefault(item => item.Id == build.Value)?.Downloads;
                addons.Add(new MinecraftInstallAddon(build.Key, build.Value, downloads));
            }
            else if (primary is null)
            {
                primary = (build.Key, build.Value);
            }
            else
            {
                _feedback.Warn("一次只能选择一个主加载器。");
                return false;
            }
        }

        string root = _installEdit?.RootDirectory ?? ReadCell(LaunchPageState.InstanceDirectoryKey);
        if (string.IsNullOrWhiteSpace(root)) { _feedback.Warn("尚未确定 Minecraft 目录。"); return false; }
        _ = _installRunCommands.Dispatch(route, new MinecraftInstallCommand(
            root,
            _selectedInstallVersion,
            primary?.Kind,
            primary?.Build,
            addons, _installEdit?.InstanceId ?? (version == _selectedInstallVersion ? null : version), _installEdit?.Fingerprint)
        { NewInstanceName = _installEdit is null ? null : version });
        // Manual starts watch their task immediately (parity with the legacy task manager).
        _intents.Emit(XsrSemanticId.Parse("ui.tasks.open"), default, XsrCorrelationId.Create());
        return true;
    }


    private bool IsKeyboardIntent(XsrUiEntityId source) => source.IsAssigned
        && _shell.Tree.IsAlive(source) && _shell.Tree.GetComponent<XsrUiInput>(source)?.IsFocusVisible == true;


    private void ClearSubpageHistory()
    {
        while (_shell.Stage.Navigation.Depth > 1) _shell.Stage.Navigation.Pop();
        _returnFocus.Clear();
        UpdateTitleBar();
    }


    private void OpenSubpage(XsrUiEntityId page, XsrUiEntityId source)
    {
        if (_shell.Stage.Navigation.Current == page) return;
        bool keyboard = IsKeyboardIntent(source);
        _returnFocus.Push(source);
        _shell.Stage.Navigation.Push(page);
        UpdateTitleBar();
        _shell.Renderer.Focus(_titleEntities["TitleBack"], keyboard);
    }


    private void OnShellStyleChanged(object? sender, EventArgs e) => UpdateTitleBar();


    private void UpdateTitleBar()
    {
        int depth = _shell.Stage.Navigation.Depth;
        XsrUiTransition main = _shell.Tree.GetComponent<XsrUiTransition>(_titleEntities["TitleMain"])!;
        XsrUiTransition title = _shell.Tree.GetComponent<XsrUiTransition>(_titleEntities["TitleSubpage"])!;
        main.Source = _titleEntities["TitleSubpage"];
        title.Source = _titleEntities["TitleMain"];
        if (depth != _titleNavigationDepth)
        {
            main.OffsetX = title.OffsetX = depth > _titleNavigationDepth ? 128 : -128;
            _titleNavigationDepth = depth;
        }
        bool subpage = _shell.Stage.Navigation.Depth > 1;
        Publish(LaunchPageState.TitleTransitionKey, subpage
            ? _shell.Tree.GetComponent<XsrUiSemantic>(_shell.Stage.Navigation.Current)?.Label ?? string.Empty : "NexaCL");
        foreach (string key in new[] { "TitleMain", "TitleBack", "TitleSubpage" })
        {
            XsrUiEntityId entity = _titleEntities[key];
            _shell.Tree.GetComponent<XsrUiElement>(entity)!.IsVisible = key == "TitleMain" ? !subpage : subpage;
            XsrUiVisualStyle style = RequireVisual(entity);
            style.Foreground = _shell.Palette.TitleBarText;
            style.FontSize = 17;
            style.FontWeight = 600;
            if (key == "TitleBack")
            {
                style.Hover = new XsrUiColor(255, 255, 255, 50);
                style.CornerRadius = XsrUiCornerRadii.Pill(30);
            }
            if (key == "TitleSubpage")
                _shell.Tree.GetComponent<XsrUiText>(entity)!.Content = subpage
                    ? _shell.Tree.GetComponent<XsrUiSemantic>(_shell.Stage.Navigation.Current)?.Label ?? string.Empty
                    : string.Empty;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }


    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.InstallPage.pxml", "install-page-loader");
        StyleInstallPage(entities);
        return (page, entities);
    }


    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadJavaInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.JavaInstallPage.pxml", "java-install-page-loader");
        StyleJavaInstallPage(entities);
        _ = _shell.Renderer.SetTextInputValue(entities["JavaInstallVersionInput"], _selectedInstallVersion);
        StyleInstallChoices(
            entities,
            ["JavaVersion1211", "JavaVersion1206", "JavaVersion1201"],
            "JavaVersion1211",
            InstallJavaTint,
            InstallJavaAccent);
        StyleInstallChoices(
            entities,
            JavaInstallLoaderKeys,
            string.Empty,
            InstallJavaTint,
            InstallJavaAccent);
        return (page, entities);
    }


    private XsrUiEntityId LoadBedrockInstallPage()
    {
        (XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities) = LoadStandalonePage(
            "Ui.BedrockInstallPage.pxml", "bedrock-install-page-loader");
        StyleBedrockInstallPage(entities);
        return page;
    }


    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadStandalonePage(
        string resource,
        string hostKey)
    {
        PxmlHostIr ir = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource(resource)));
        XsrUiEntityId host = _shell.Tree.Create(hostKey);
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(page, entity =>
        {
            string key = _shell.Tree.Name(entity);
            if (key.Length > 0)
            {
                entities[key] = entity;
            }

            return true;
        });
        return (page, entities);
    }


    /// <summary>
    /// Applies a restrained, solid-surface hierarchy: one obvious primary choice per card,
    /// compact supporting copy, and no glass material. Motion itself remains renderer-owned
    /// (press/hover and navigator presentation), so this styling never invents a second clock.
    /// </summary>
    private void StyleInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        ApplyVisual(entities["InstallJavaChoice"], InstallJavaTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder, hover: InstallJavaHover);
        ApplyVisual(entities["InstallBedrockChoice"], InstallBedrockTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder, hover: InstallBedrockHover);
        ApplyVisual(entities["InstallJavaArtwork"], XsrUiColor.Transparent, InstallJavaAccent,
            XsrUiCornerRadii.Inset);
        ApplyVisual(entities["InstallBedrockArtwork"], XsrUiColor.Transparent, InstallBedrockAccent,
            XsrUiCornerRadii.Inset);
        StyleText(entities, "InstallJavaTitle", PrimaryText, 28, 650);
        StyleText(entities, "InstallBedrockTitle", PrimaryText, 28, 650);
        AlignText(entities, "InstallJavaTitle", XsrUiTextAlignment.Center);
        AlignText(entities, "InstallBedrockTitle", XsrUiTextAlignment.Center);
    }


    private void StyleJavaInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        ApplyVisual(entities["JavaInstallVersionInput"], new(255, 255, 255), PrimaryText,
            XsrUiCornerRadii.Inset, border: CardBorder);
        StyleText(entities, "JavaInstallVersionInput", PrimaryText, 14);
        ApplyVisual(entities["JavaInstallStart"], InstallJavaAccent, new(255, 255, 255),
            XsrUiCornerRadii.Pill(40), hover: LaunchButtonHover);
        StyleText(entities, "JavaInstallStart", new(255, 255, 255), 13, 650);
        AlignText(entities, "JavaInstallStart", XsrUiTextAlignment.Center);

        // Navigation, catalog and commit action are separate spatial groups. The pager and
        // press transitions remain owned by UI.Next, preserving interruptible motion.
        ApplyVisual(entities["JavaInstallPagerTabs"], ProfileSurface, PrimaryText,
            10);
        _shell.Tree.SetComponent(entities["JavaInstallPagerTabs"], new XsrUiSegmentedTrack(entities["JavaInstallThumb"]));
        _shell.Tree.SetComponent(entities["JavaInstallThumb"], new XsrUiTransition());
        ApplyVisual(entities["JavaInstallThumb"], new(255, 255, 255), PrimaryText, 8);
        ApplyVisual(entities["JavaInstallEntryRow"], XsrUiColor.Transparent, PrimaryText,
            XsrUiCornerRadii.Surface);

        ApplyVisual(entities["JavaMinecraftVersions"], new(255, 255, 255), PrimaryText, XsrUiCornerRadii.Surface, border: CardBorder);
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            if (entities.TryGetValue(subpage.PageKey, out XsrUiEntityId page))
            {
                ApplyVisual(page, XsrUiColor.Transparent, PrimaryText, 0);
            }

            StyleText(entities, subpage.PageKey + "Title", PrimaryText, 19, 650);
            _shell.Tree.SetComponent(entities[subpage.TabKey], new XsrUiSegmentReveal(_shell.Tree.GetComponent<XsrUiElement>(entities[subpage.TabKey])!.Width!.Value));
            ApplyPagerTab(entities, subpage.TabKey, active: subpage.PageKey == "JavaMinecraftPage");
        }
    }


    private void StyleBedrockInstallPage(Dictionary<string, XsrUiEntityId> entities)
    {
        StyleText(entities, "BedrockInstallTitle", InstallBedrockAccent, 20, 650);
        StyleText(entities, "BedrockInstallDescription", SecondaryText, 13);
        SetWrap(entities, "BedrockInstallDescription");
        ApplyVisual(entities["BedrockInstallCard"], InstallBedrockTint, PrimaryText,
            XsrUiCornerRadii.Surface, border: CardBorder);
        StyleText(entities, "BedrockInstallReturn", SecondaryText, 13);
        SetWrap(entities, "BedrockInstallReturn");
    }


    private void UpdateJavaInstallSubpageVisibility(string? preferredPage = null)
    {
        XsrUiEntityId pagerEntity = _javaInstallEntities["JavaInstallPager"];
        XsrUiEntityId[] previousPages = VisibleJavaInstallPages(pagerEntity);
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            bool visible = ShouldShowJavaInstallSubpage(subpage);
            SetInstallEntityVisible(subpage.PageKey, visible);
            SetInstallEntityVisible(subpage.TabKey, visible);
        }

        if (_selectedInstallLoader != "Fabric")
        {
            _selectedInstallAddons.Remove("Fabric API");
            UpdateInstallAddonStyle("Fabric API", "JavaFabricApiSelect", selected: false);
        }
        if (_selectedInstallLoader != "Quilt")
        {
            _selectedInstallAddons.Remove("QSL");
            UpdateInstallAddonStyle("QSL", "JavaQslSelect", selected: false);
        }

        string target = preferredPage ?? _activeJavaInstallPage;
        if (!_javaInstallEntities.TryGetValue(target, out XsrUiEntityId targetEntity)
            || !IsInstallEntityVisible(targetEntity))
        {
            target = "JavaMinecraftPage";
        }

        var nextPages = VisibleJavaInstallPages(pagerEntity);
        if (!previousPages.SequenceEqual(nextPages))
            _shell.Renderer.RebasePagerPage(pagerEntity, Array.IndexOf(nextPages, _javaInstallEntities[target]));
        ShowJavaInstallSubpage(target);
    }


    private bool ShouldShowJavaInstallSubpage(JavaInstallSubpage subpage)
    {
        if (subpage.PageKey == "JavaMinecraftPage") return true;
        if (!_installGameChosen) return false;
        InstallLoader? loader = subpage.PageKey switch
        {
            "JavaFabricApiPage" => InstallLoader.FabricApi,
            "JavaOptiFabricPage" => InstallLoader.OptiFabric,
            "JavaQslPage" => InstallLoader.Qsl,
            _ => ParseInstallLoader(subpage.Loader)
        };
        return QueryInstallEligibility()?.Loaders.Any(item => item.Loader == loader && item.Visible) == true;
    }


    private void SetInstallEntityVisible(string key, bool visible)
    {
        if (!_javaInstallEntities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        if (_shell.Tree.GetComponent<XsrUiSegmentReveal>(entity) is not null)
        {
            _shell.Renderer.SetSegmentExpanded(entity, visible, immediate: !_attached);
            return;
        }
        XsrUiElement element = _shell.Tree.GetComponent<XsrUiElement>(entity) ?? new XsrUiElement();
        if (element.IsVisible == visible)
        {
            return;
        }

        element.IsVisible = visible;
        _shell.Tree.SetComponent(entity, element);
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }


    private bool IsInstallEntityVisible(XsrUiEntityId entity) =>
        _shell.Tree.GetComponent<XsrUiElement>(entity)?.IsVisible ?? true;


    private XsrUiEntityId[] VisibleJavaInstallPages(XsrUiEntityId pager) =>
        [.. _shell.Tree.Children(pager).Where(IsInstallEntityVisible)];


    private void RefreshJavaInstallPresentation()
    {
        if (!_javaInstallEntities.TryGetValue("JavaInstallPager", out XsrUiEntityId pagerEntity)
            || _shell.Tree.GetComponent<XsrUiPager>(pagerEntity) is not { } pager)
        {
            return;
        }

        XsrUiEntityId[] visiblePages = VisibleJavaInstallPages(pagerEntity);
        if (visiblePages.Length == 0)
        {
            return;
        }

        int current = Math.Clamp(pager.PageIndex, 0, visiblePages.Length - 1);
        JavaInstallSubpage? active = JavaInstallSubpages.FirstOrDefault(subpage =>
            _javaInstallEntities.TryGetValue(subpage.PageKey, out XsrUiEntityId page)
            && page == visiblePages[current]);
        if (active is null)
        {
            return;
        }

        if (current == _presentedJavaInstallPage && _activeJavaInstallPage == active.PageKey)
        {
            return;
        }

        _presentedJavaInstallPage = current;
        _activeJavaInstallPage = active.PageKey;
        foreach (JavaInstallSubpage subpage in JavaInstallSubpages)
        {
            ApplyPagerTab(_javaInstallEntities, subpage.TabKey, subpage.PageKey == active.PageKey);
        }
    }


    private void ApplyPagerTab(Dictionary<string, XsrUiEntityId> entities, string key, bool active)
    {
        if (!entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        ApplyVisual(entity,
            XsrUiColor.Transparent,
            active ? BadgeText : DesktopUiPalette.CapsuleForeground,
            0,
            hover: XsrUiColor.Transparent);
        if (active && entities.TryGetValue("JavaInstallPagerTabs", out XsrUiEntityId tabs))
            _shell.Tree.GetComponent<XsrUiSegmentedTrack>(tabs)!.Selected = entity;
        StyleText(entity, active ? BadgeText : DesktopUiPalette.CapsuleForeground, 13, active ? 650 : 500);
        AlignText(entity, XsrUiTextAlignment.Center);
        XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
        selection.IsSelected = active;
        _shell.Tree.SetComponent(entity, selection);
        XsrUiSemantic? semantic = _shell.Tree.GetComponent<XsrUiSemantic>(entity);
        if (semantic is not null)
        {
            string name = JavaInstallSubpageName(key);
            semantic.Label = active
                ? $"{name}，当前页"
                : $"查看 {name} 设置";
        }

        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }


    private void UpdateInstallAddonStyle(string addon, string key, bool selected)
    {
        if (!_javaInstallEntities.TryGetValue(key, out XsrUiEntityId entity))
        {
            return;
        }

        ApplyVisual(entity,
            selected ? InstallJavaTint : new(255, 255, 255),
            selected ? InstallJavaAccent : PrimaryText,
            XsrUiCornerRadii.Pill(36),
            border: selected ? InstallSelectedBorder : CardBorder,
            hover: selected ? InstallJavaTint : PickerBackground);
        StyleText(entity, selected ? InstallJavaAccent : PrimaryText, 13, selected ? 650 : 550);
        AlignText(entity, XsrUiTextAlignment.Center);
        XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
        selection.IsSelected = selected;
        _shell.Tree.SetComponent(entity, selection);
        XsrUiSemantic? semantic = _shell.Tree.GetComponent<XsrUiSemantic>(entity);
        if (semantic is not null)
        {
            semantic.Label = selected ? $"移除 {addon}" : $"添加 {addon}";
        }
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }


    private static string JavaInstallSubpageName(string tabKey) => tabKey switch
    {
        "JavaMinecraftTab" => "Minecraft",
        "JavaForgeTab" => "Forge",
        "JavaCleanroomTab" => "Cleanroom",
        "JavaNeoForgeTab" => "NeoForge",
        "JavaFabricTab" => "Fabric",
        "JavaLegacyFabricTab" => "Legacy Fabric",
        "JavaFabricApiTab" => "Fabric API",
        "JavaQuiltTab" => "Quilt",
        "JavaQslTab" => "QSL",
        "JavaOptiFabricTab" => "OptiFabric",
        "JavaLabyModTab" => "LabyMod",
        "JavaOptiFineTab" => "OptiFine",
        "JavaLiteLoaderTab" => "LiteLoader",
        _ => "安装选项",
    };


    private void StyleInstallChoices(
        Dictionary<string, XsrUiEntityId> entities,
        IReadOnlyList<string> keys,
        string selectedKey,
        XsrUiColor selectedBackground,
        XsrUiColor accent)
    {
        foreach (string key in keys)
        {
            if (!entities.TryGetValue(key, out XsrUiEntityId entity))
            {
                continue;
            }

            bool selected = key == selectedKey;
            if (key.StartsWith("JavaVersion", StringComparison.Ordinal))
            {
                ApplyVisual(entity, selected ? ProfileSurface : XsrUiColor.Transparent, PrimaryText,
                    XsrUiCornerRadii.Inset, hover: PickerBackground);
                StyleText(entities, key + "Name", selected ? BadgeText : PrimaryText, 15, selected ? 600 : 400);
                XsrUiEntityId check = entities[key + "Check"];
                ApplyVisual(check, XsrUiColor.Transparent, selected ? BadgeText : XsrUiColor.Transparent, 0);
                XsrUiSelection rowSelection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
                rowSelection.IsSelected = selected;
                _shell.Tree.SetComponent(entity, rowSelection);
                _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
                continue;
            }

            ApplyVisual(entity,
                selected ? selectedBackground : new(255, 255, 255),
                selected ? accent : PrimaryText,
                XsrUiCornerRadii.Inset,
                border: selected ? InstallSelectedBorder : CardBorder,
                hover: selected ? selectedBackground : PickerBackground);
            StyleText(entity, selected ? accent : PrimaryText, 13, selected ? 650 : 550);
            XsrUiSelection selection = _shell.Tree.GetComponent<XsrUiSelection>(entity) ?? new XsrUiSelection();
            selection.IsSelected = selected;
            _shell.Tree.SetComponent(entity, selection);
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }


    private void SetWrap(Dictionary<string, XsrUiEntityId> entities, string key)
    {
        if (entities.TryGetValue(key, out XsrUiEntityId entity))
        {
            RequireVisual(entity).WrapText = true;
            _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }


    private XsrUiEntityId LoadVersionSubpage(string key, string title)
    {
        PxmlHostIr template = PxmlCompiler.Compile(PxmlParser.Parse(ReadEmbeddedResource("Ui.VersionSubpage.pxml")));
        PxmlIrNode Project(PxmlIrNode node) => node with
        {
            Key = node.Key == "VersionSubpage" ? key : node.Key,
            Label = node.Key == "VersionSubpage" ? title : node.Label,
            Content = node.Key == "MigrationTitle" ? title + " · 尚未迁移" : node.Content,
            Children = [.. node.Children.Select(Project)],
        };
        XsrUiEntityId parent = _shell.Tree.Create("subpage-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(new PxmlHostIr(Project(template.Root)), _shell.Tree, _store, parent);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(parent);
        _shell.Tree.Walk(page, entity =>
        {
            string name = _shell.Tree.Name(entity);
            XsrUiVisualStyle style = new() { Foreground = PrimaryText, FontSize = 14, TextAlignment = XsrUiTextAlignment.Center };
            if (name == "MigrationCard") { style.Background = new(245, 248, 252); style.CornerRadius = 20; }
            if (name == "MigrationTitle") { style.FontSize = 22; style.FontWeight = 600; }
            if (name == "MigrationMessage") { style.Foreground = SecondaryText; style.WrapText = true; }
            if (name == "MigrationIcon") style.Foreground = LaunchButtonBackground;
            if (name == "MigrationReturn") { style.Background = LaunchButtonBackground; style.Foreground = new(255, 255, 255); style.CornerRadius = 19; }
            _shell.Tree.SetComponent(entity, style);
            return true;
        });
        return page;
    }


    /// <summary>
    /// Loads the dedicated launching page: the legacy launching card (centered 420px card with
    /// the progress bar, key/value rows, trivia hint, and cancel) as its own navigation page.
    /// </summary>
    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadLaunchingPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchingPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId parent = _shell.Tree.Create("launching-page-loader");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, parent);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(parent);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(
            page,
            entity =>
            {
                string key = _shell.Tree.Name(entity);
                if (key.Length > 0)
                {
                    entities[key] = entity;
                }

                return true;
            });

        XsrUiVisualStyle card = RequireVisual(entities["LaunchingCard"]);
        card.Background = CardBackground;
        card.Foreground = PrimaryText;
        card.Border = CardBorder;
        card.BorderWidth = 1;
        card.Surface = XsrUiSurfaceKind.Solid;
        card.CornerRadius = XsrUiCornerRadii.Surface;
        _shell.Tree.MarkDirty(entities["LaunchingCard"], XsrUiDirtyKinds.Paint);
        StyleText(entities, "LaunchingTitle", PrimaryText, 22, 600);
        StyleText(entities, "LaunchingName", SecondaryText, 14);
        foreach ((string label, string value) in new[]
        {
            ("LaunchingStageLabel", "LaunchingStageValue"),
            ("LaunchingMethodLabel", "LaunchingMethodValue"),
            ("LaunchingPercentLabel", "LaunchingPercentValue"),
            ("LaunchingSpeedLabel", "LaunchingSpeedValue"),
        })
        {
            StyleText(entities, label, SecondaryText, 12);
            StyleText(entities, value, PrimaryText, 12);
        }

        StyleText(entities, "LaunchingHintTitle", SecondaryText, 11, 600);
        StyleText(entities, "LaunchingHintValue", SecondaryText, 12);
        AlignText(entities, "LaunchingHintTitle", XsrUiTextAlignment.Center);
        AlignText(entities, "LaunchingHintValue", XsrUiTextAlignment.Center);
        ApplyVisual(entities["LaunchProgressTrack"], LaunchProgressTrack, PrimaryText, cornerRadius: 2);
        ApplyVisual(entities["LaunchProgressFill"], LaunchProgressFill, LaunchProgressFill, cornerRadius: 2);
        ApplyVisual(entities["LaunchingHintBox"], PickerBackground, PrimaryText, XsrUiCornerRadii.Inset);
        ApplyVisual(entities["LaunchingCancelButton"], PickerBackground, PrimaryText,
            XsrUiCornerRadii.Pill(40));
        StyleText(entities, "LaunchingCancelButton", PrimaryText, 14, 600);
        AlignText(entities, "LaunchingCancelButton", XsrUiTextAlignment.Center);
        return (page, entities);
    }

}
