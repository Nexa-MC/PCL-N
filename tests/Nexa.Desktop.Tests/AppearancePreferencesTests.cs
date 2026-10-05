using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Settings;
using Nexa.Services.Tasks;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void AppearanceSessionAppliesCommittedModesAndSurvivesFailedSaves()
    {
        var port = new AppearanceSettingsPort();
        var builder = new XsrStateStoreBuilder();
        var schema = LauncherDefaults.CreateSchema();
        SettingsService.DeclareState(builder, schema);
        SettingsPolicyContract.DeclareState(builder);
        var store = builder.Build();
        var settings = new SettingsService(store, schema, port);
        var policy = new SettingsPolicyService(settings);
        var shell = new XsrUiShell(store);
        var native = new AppearanceSource();
        using var session = new DesktopAppearanceSession(shell, store, native);
        AssertFalse(shell.Renderer.ColorScheme.IsDark); // Unavailable system preference falls back.
        AssertEqual(XsrUiThemeMode.System, native.Modes.Single());
        void Pump() => shell.Render(new(1000, 650));
        void Set(string key, string value) => AssertTrue(policy.Set(new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
        native.Change(true); Pump(); AssertTrue(shell.Renderer.ColorScheme.IsDark);
        Set("appearance.theme-mode", "0"); Pump(); AssertFalse(shell.Renderer.ColorScheme.IsDark);
        native.Change(false); native.Change(true); Pump(); AssertFalse(shell.Renderer.ColorScheme.IsDark);
        Set("appearance.accent", "purple"); Pump(); AssertEqual(XsrUiAccent.Purple, shell.Renderer.ColorScheme.Accent);
        Set("appearance.theme-mode", "1"); Pump(); AssertTrue(shell.Renderer.ColorScheme.IsDark);
        native.Change(false); Pump(); AssertTrue(shell.Renderer.ColorScheme.IsDark);
        var scheme = shell.Renderer.ColorScheme;
        port.Fail = true;
        AssertFalse(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
        AssertFalse(policy.Set(new("appearance.accent", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "orange"))).IsSuccess);
        Pump(); AssertEqual(scheme, shell.Renderer.ColorScheme);
        port.Fail = false;
        Set("appearance.theme-mode", "2"); Pump(); AssertFalse(shell.Renderer.ColorScheme.IsDark);
        native.Change(true); Pump(); AssertTrue(shell.Renderer.ColorScheme.IsDark);
        int calls = native.Modes.Count;
        for (int i = 0; i < 10; i++) Pump();
        AssertEqual(calls, native.Modes.Count);
        session.Dispose(); AssertEqual(default(XsrUiColorScheme), shell.Renderer.ColorScheme);
        native.Change(false); Set("appearance.theme-mode", "0"); Pump();
        AssertEqual(calls, native.Modes.Count);
        Set("appearance.theme-mode", "1");
        using var reopened = new DesktopAppearanceSession(shell, store, native);
        AssertTrue(shell.Renderer.ColorScheme.IsDark);
        AssertEqual(XsrUiAccent.Purple, shell.Renderer.ColorScheme.Accent);

        var empty = new XsrStateStoreBuilder().Build();
        var firstRun = new XsrUiShell(empty);
        using var firstRunSession = new DesktopAppearanceSession(firstRun, empty, new AppearanceSource(true));
        AssertTrue(firstRun.Renderer.ColorScheme.IsDark);
    }

    private static void AppearanceSceneProjectionPreservesColorsGeometryAndCaching()
    {
        var shell = new XsrUiShell(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        var page = shell.Tree.Create("AppearancePage");
        shell.Tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical));
        var input = shell.Tree.Create("AppearanceInput"); shell.Tree.Attach(input, page);
        shell.Tree.SetComponent(input, new XsrUiElement { Width = 200, Height = 40 });
        shell.Tree.SetComponent(input, new XsrUiTextInput());
        shell.Renderer.SetTextInputValue(input, "retained draft");
        shell.Tree.SetComponent(input, new XsrUiInput { Focusable = true, Clickable = true });
        // The real LibrarySearch/directory editor tokens must not retain their light surface
        // after their ink is projected to dark-mode white.
        var inputStyle = new XsrUiVisualStyle
        {
            Background = new(240, 244, 250),
            Foreground = new(38, 49, 65),
            Hover = new(237, 243, 253),
            Surface = XsrUiSurfaceKind.Solid,
        };
        shell.Tree.SetComponent(input, inputStyle);
        var custom = shell.Tree.Create("AppearanceCustom"); shell.Tree.Attach(custom, page);
        shell.Tree.SetComponent(custom, new XsrUiElement { Width = 200, Height = 40 });
        var customStyle = new XsrUiVisualStyle { Background = new(78, 23, 92, 130), Foreground = new(203, 73, 153), Surface = XsrUiSurfaceKind.Solid };
        shell.Tree.SetComponent(custom, customStyle);
        var text = new XsrUiText("status and accent")
        {
            Runs = [new(0, 6, new(207, 47, 54)), new(7, 3, new(11, 91, 203))],
        };
        shell.Tree.SetComponent(custom, text);
        shell.Stage.Navigation.Replace(page);
        var original = shell.Render(new(1000, 650));
        AssertTrue(shell.Renderer.Focus(input));
        original = shell.Render(new(1000, 650));
        var inputNode = original.Nodes.Single(node => node.Entity == input);
        foreach (var accent in Enum.GetValues<XsrUiAccent>())
        {
            shell.Renderer.ColorScheme = new(true, accent);
            var dark = shell.Render(new(1000, 650));
            AssertEqual(0, shell.Renderer.LastLayoutVisits);
            var darkInput = dark.Nodes.Single(node => node.Entity == input);
            AssertEqual(inputNode.Rect, darkInput.Rect);
            AssertEqual(input, shell.Renderer.Focused);
            AssertEqual("retained draft", darkInput.TextInput!.Value.DisplayText);
            AssertTrue(darkInput.VisualStyle.Background != inputNode.VisualStyle.Background);
            AssertEqual(new XsrUiColor(42, 49, 62), darkInput.VisualStyle.Background);
            AssertTrue(AppearanceContrast(darkInput.VisualStyle.Foreground, darkInput.VisualStyle.Background) >= 4.5);
            AssertTrue(AppearanceContrast(darkInput.VisualStyle.Foreground, darkInput.VisualStyle.Hover) >= 4.5);
            var customNode = dark.Nodes.Single(node => node.Entity == custom);
            AssertEqual(customStyle.Background, customNode.VisualStyle.Background);
            AssertEqual(customStyle.Foreground, customNode.VisualStyle.Foreground);
            AssertEqual(new XsrUiColor(207, 47, 54), text.Runs[0].Foreground);
            AssertEqual(shell.Renderer.ColorScheme.AccentText, customNode.TextRuns![1].Foreground);
            AssertEqual(inputNode.VisualStyle, inputStyle.Snapshot());
            AssertTrue(ReferenceEquals(dark, shell.Render(new(1000, 650))));
        }
        shell.Renderer.ColorScheme = default;
        var restored = shell.Render(new(1000, 650));
        AssertEqual(inputNode.VisualStyle, restored.Nodes.Single(node => node.Entity == input).VisualStyle);
        AssertTrue(AppearanceContrast(inputNode.VisualStyle.Foreground, inputNode.VisualStyle.Background) >= 4.5);
        AssertTrue(AppearanceContrast(inputNode.VisualStyle.Foreground, inputNode.VisualStyle.Hover) >= 4.5);
        AssertEqual(original.Nodes.Single(node => node.Entity == custom).TextRuns![0].Foreground,
            restored.Nodes.Single(node => node.Entity == custom).TextRuns![0].Foreground);
        AssertEqual(new XsrUiColor(255, 255, 255), restored.Nodes.First(node => node.Text == shell.Title).VisualStyle.Foreground);

        // All selectable dark-mode accents maintain readable links and inverse action text.
        foreach (var accent in Enum.GetValues<XsrUiAccent>())
        {
            var scheme = new XsrUiColorScheme(true, accent);
            var background = scheme.Project(inputStyle.Snapshot()).Background;
            AssertTrue(AppearanceContrast(scheme.AccentText, background) >= 4.5);
            AssertTrue(AppearanceContrast(new(255, 255, 255), scheme.AccentFill) >= 4.5);
        }

        AppearanceOwnedPagesKeepReadablePairedTokens();
    }

    private static void AppearanceOwnedPagesKeepReadablePairedTokens()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("chosen"), Instance("other")]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var shell = fixture.Shell;
        shell.Renderer.ReducedMotion = true;
        XsrUiSize size = new(1280, 1000);
        shell.Renderer.Activate(FindByKey(shell, shell.Render(size), "InstanceListButton").Entity);
        var originalVersions = shell.Render(size);
        var originalRow = FindByKey(shell, originalVersions, "LibraryRow:version:chosen");
        var originalSearch = FindByKey(shell, originalVersions, "LibrarySearch");
        AssertEqual(new XsrUiColor(231, 240, 255), originalRow.VisualStyle.Background);
        foreach (var accent in Enum.GetValues<XsrUiAccent>())
        {
            shell.Renderer.ColorScheme = new(true, accent);
            var scene = shell.Render(size);
            var row = FindByKey(shell, scene, "LibraryRow:version:chosen");
            AssertTrue(row.VisualStyle.Background != originalRow.VisualStyle.Background);
            foreach (string label in new[] { "LibraryRowName:version:chosen", "LibraryRowDetail:version:chosen" })
                AssertTrue(AppearanceContrast(FindByKey(shell, scene, label).VisualStyle.Foreground, row.VisualStyle.Background) >= 4.5);
            var search = FindByKey(shell, scene, "LibrarySearch");
            AssertTrue(AppearanceContrast(search.VisualStyle.Foreground, search.VisualStyle.Background) >= 4.5);
            AssertTrue(AppearanceContrast(search.VisualStyle.Foreground, search.VisualStyle.Hover) >= 4.5);
            AssertEqual(originalRow.VisualStyle, shell.Tree.GetComponent<XsrUiVisualStyle>(row.Entity)!.Snapshot());
        }
        shell.Renderer.ColorScheme = default;
        var restoredVersions = shell.Render(size);
        AssertEqual(originalRow.VisualStyle, FindByKey(shell, restoredVersions, "LibraryRow:version:chosen").VisualStyle);
        AssertEqual(originalSearch.VisualStyle, FindByKey(shell, restoredVersions, "LibrarySearch").VisualStyle);

        using var bubble = new DesktopTaskBubblePresenter(shell, fixture.Store);
        using var taskRuntime = TaskCenterRuntimeComposer.Compose(fixture.Foundation.Host);
        using var tasks = new TaskCenterController(shell, fixture.Intents, taskRuntime.Commands, fixture.Store, bubble);
        using var running = fixture.Foundation.Host.Tasks.Begin(new TaskCenterStart("appearance-running", "Appearance running", ["Download"]));
        running.Report("Download", "Downloading", .5, 2, 4, 2048);
        using var finished = fixture.Foundation.Host.Tasks.Begin(new TaskCenterStart("appearance-finished", "Appearance finished", ["Download"]));
        finished.Complete("Saved");
        using var failed = fixture.Foundation.Host.Tasks.Begin(new TaskCenterStart("appearance-failed", "Appearance failed", ["Download"]));
        failed.Fail("Appearance failure details");
        Emit(fixture.Intents, "ui.tasks.open");
        var originalTasks = shell.Render(size);

        XsrUiSceneNode CardText(XsrUiScene scene, string id, string key)
        {
            XsrUiEntityId card = FindByKey(shell, scene, "task-card:" + id).Entity;
            return scene.Nodes.Single(node => shell.Tree.Name(node.Entity) == key && IsDescendant(node.Entity, card));
        }
        bool IsDescendant(XsrUiEntityId entity, XsrUiEntityId parent)
        {
            while ((entity = shell.Tree.Parent(entity)).IsAssigned)
                if (entity == parent) return true;
            return false;
        }

        foreach (var accent in Enum.GetValues<XsrUiAccent>())
        {
            shell.Renderer.ColorScheme = new(true, accent);
            var scene = shell.Render(size);
            foreach (string id in new[] { "appearance-running", "appearance-finished", "appearance-failed" })
            {
                var card = FindByKey(shell, scene, "task-card:" + id);
                foreach (string key in new[] { "TaskCardTitle", "TaskCardStage", "TaskCardPercent" })
                    AssertTrue(AppearanceContrast(CardText(scene, id, key).VisualStyle.Foreground, card.VisualStyle.Background) >= 4.5);
                if (id == "appearance-failed")
                    AssertTrue(AppearanceContrast(CardText(scene, id, "TaskCardError").VisualStyle.Foreground, card.VisualStyle.Background) >= 4.5);
            }
            var clear = FindByKey(shell, scene, "TaskCenterClear");
            AssertTrue(AppearanceContrast(clear.VisualStyle.Foreground, clear.VisualStyle.Background) >= 4.5);
        }
        shell.Renderer.ColorScheme = default;
        var restoredTasks = shell.Render(size);
        AssertEqual(FindByKey(shell, originalTasks, "task-card:appearance-running").VisualStyle,
            FindByKey(shell, restoredTasks, "task-card:appearance-running").VisualStyle);

        // Further real owned pairs: consent choices, JVM/hook editors, graph-canvas ink,
        // resource release badges and account-delete hover text.
        XsrUiVisualStyleSnapshot[] pairs =
        [
            new XsrUiVisualStyle { Background = new(243, 246, 250), Foreground = new(52, 61, 74) }.Snapshot(),
            new XsrUiVisualStyle { Background = new(244, 247, 251), Foreground = new(43, 51, 64) }.Snapshot(),
            new XsrUiVisualStyle { Background = new(247, 249, 252), Foreground = new(43, 51, 64) }.Snapshot(),
            new XsrUiVisualStyle { Background = new(255, 255, 255), Foreground = new(40, 135, 90) }.Snapshot(),
            new XsrUiVisualStyle { Background = new(255, 224, 224), Foreground = new(96, 108, 124) }.Snapshot(),
        ];
        foreach (var pair in pairs)
        {
            AssertEqual(pair, default(XsrUiColorScheme).Project(pair));
            foreach (var accent in Enum.GetValues<XsrUiAccent>())
            {
                var projected = new XsrUiColorScheme(true, accent).Project(pair);
                AssertTrue(AppearanceContrast(projected.Foreground, projected.Background) >= 4.5);
            }
        }
    }

    private static void AppearanceControlsUseConfirmedAccessibleModes()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Controller.Dispose();
        using var session = new DesktopAppearanceSession(fixture.Shell, fixture.Store, new AppearanceSource());
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.appearance").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        var automatic = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.theme-mode.2");
        var dark = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.theme-mode.1");
        AssertEqual(XsrUiSemanticRole.RadioButton, dark.Role);
        AssertTrue(automatic.IsSelected); AssertFalse(dark.IsSelected);
        Emit(fixture.Intents, "ui.settings.choice", dark.Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<int>("UiDarkMode").Value == 1, TimeSpan.FromSeconds(5)));
        // The durable raw commit can precede the controller's policy-query result.
        // Pump the real frame boundary until both presentation consumers confirm it.
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return !settings.SettingsWritePending
                && FindByKey(fixture.Shell, scene, "SettingsOption.appearance.theme-mode.1").IsSelected
                && fixture.Shell.Renderer.ColorScheme.IsDark;
        }, TimeSpan.FromSeconds(5)));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsOption.appearance.theme-mode.1").IsSelected);
        AssertTrue(fixture.Shell.Renderer.ColorScheme.IsDark);
        var purple = FindByKey(fixture.Shell, scene, "SettingsOption.appearance.accent.purple");
        Emit(fixture.Intents, "ui.settings.choice", purple.Entity);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("UiAccentColor").Value == "purple", TimeSpan.FromSeconds(5)));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 650));
            return !settings.SettingsWritePending
                && FindByKey(fixture.Shell, scene, "SettingsOption.appearance.accent.purple").IsSelected
                && fixture.Shell.Renderer.ColorScheme.Accent == XsrUiAccent.Purple;
        }, TimeSpan.FromSeconds(5)));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsOption.appearance.accent.purple").IsSelected);
        AssertEqual(XsrUiAccent.Purple, fixture.Shell.Renderer.ColorScheme.Accent);
    }

    private static double AppearanceContrast(XsrUiColor foreground, XsrUiColor background)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        static double Luminance(XsrUiColor c) => .2126 * Channel(c.Red) + .7152 * Channel(c.Green) + .0722 * Channel(c.Blue);
        double fg = Luminance(foreground), bg = Luminance(background);
        return (Math.Max(fg, bg) + .05) / (Math.Min(fg, bg) + .05);
    }

    private sealed class AppearanceSource(bool? dark = null) : IXsrUiSystemAppearance
    {
        public bool? IsSystemDark { get; private set; } = dark;
        public event Action? SystemAppearanceChanged;
        public List<XsrUiThemeMode> Modes { get; } = [];
        public void SetThemeMode(XsrUiThemeMode mode) => Modes.Add(mode);
        public void Change(bool? value) { IsSystemDark = value; SystemAppearanceChanged?.Invoke(); }
    }

    private sealed class AppearanceSettingsPort : ISettingsPort
    {
        private readonly InMemorySettingsPort _inner = new();
        public bool Fail { get; set; }
        public IReadOnlyDictionary<string, string> Load() => _inner.Load();
        public void Save(IReadOnlyDictionary<string, string> values) { if (Fail) throw new IOException("fixture appearance persistence failure"); _inner.Save(values); }
    }
}
