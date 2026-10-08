using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    // Geometry/focus acceptance must use the same real page controllers as production.
    private sealed class LaunchFixtureNavigationPages : IDisposable
    {
        private readonly SettingsPageController _settings, _versionSettings;
        private readonly WardrobePageController _wardrobe;
        internal LaunchFixtureNavigationPages(LaunchPageFixture fixture)
        {
            _settings = new(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
            _versionSettings = new(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback,
                () => ((MinecraftLibrarySnapshot?)fixture.Store.ReadAppliedValue(fixture.Store.Resolve(MinecraftLibraryContract.StateKey)))?.SelectedInstance?.DirectoryPath);
            _wardrobe = new(fixture.Shell, fixture.Intents, fixture.Onboarding.Queries!, fixture.Onboarding.Commands, fixture.Store, fixture.Feedback);
            fixture.Controller.SettingsPage = _settings.Page;
            fixture.Controller.VersionSettingsPage = _versionSettings.Page;
            fixture.Controller.WardrobePage = _wardrobe.Page;
        }
        public void Dispose() { _wardrobe.Dispose(); _versionSettings.Dispose(); _settings.Dispose(); }
    }

    private static void LaunchNavigationRejectsUnknownAndUnboundWithoutChangingPage()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("playable")]), addProfile: true);
        fixture.Shell.Renderer.ReducedMotion = true; var scene = fixture.Shell.Render(new(850, 500));
        XsrUiEntityId home = fixture.Shell.Stage.Navigation.Current;
        var wardrobe = FindByKey(fixture.Shell, scene, "AccountWardrobe").Entity;
        AssertTrue(fixture.Shell.Renderer.Focus(wardrobe));
        Emit(fixture.Intents, "ui.account.wardrobe", wardrobe);
        AssertEqual(home, fixture.Shell.Stage.Navigation.Current); AssertEqual(1, fixture.Shell.Stage.Navigation.Depth);
        AssertEqual(wardrobe, fixture.Shell.Renderer.Focused);
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message == "更衣橱页面未绑定。"));
        Emit(fixture.Intents, "ui.launch.settings");
        AssertEqual(home, fixture.Shell.Stage.Navigation.Current);
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message == "版本设置页面未绑定。"));
        Emit(fixture.Intents, "ui.launch.instances"); scene = fixture.Shell.Render(new(850, 500));
        XsrUiEntityId list = fixture.Shell.Stage.Navigation.Current, focus = fixture.Shell.Renderer.Focused;
        AssertEqual(2, fixture.Shell.Stage.Navigation.Depth);
        foreach (string route in new[] { "ui.navigation.settings", "ui.navigation.community", "ui.navigation.unregistered" })
        {
            int count = fixture.Shell.Tree.Count;
            Emit(fixture.Intents, route);
            AssertEqual(list, fixture.Shell.Stage.Navigation.Current); AssertEqual(2, fixture.Shell.Stage.Navigation.Depth);
            AssertEqual(focus, fixture.Shell.Renderer.Focused); AssertEqual(count, fixture.Shell.Tree.Count);
        }
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message == "无法导航：未识别的页面入口。"));
        int notifications = fixture.Feedback.Snapshot().Notifications.Count;
        Emit(fixture.Intents, "ui.navigation.expand"); AssertEqual(notifications, fixture.Feedback.Snapshot().Notifications.Count);
        Emit(fixture.Intents, "ui.page.back"); AssertEqual(home, fixture.Shell.Stage.Navigation.Current);
        scene = fixture.Shell.Render(new(850, 500));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("尚未迁移", StringComparison.Ordinal) == true));
        AssertFalse(HasKey(fixture.Shell, scene, "MigrationCard"));
    }

    private static void LaunchNavigationBindingsBorrowRealPagesAndPreserveRepeatedAssignments()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]), addProfile: true);
        using var first = new WardrobePageController(fixture.Shell, fixture.Intents, fixture.Onboarding.Queries!, fixture.Onboarding.Commands, fixture.Store, fixture.Feedback);
        using var second = new WardrobePageController(fixture.Shell, fixture.Intents, fixture.Onboarding.Queries!, fixture.Onboarding.Commands, fixture.Store, fixture.Feedback);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => "instance-A");
        fixture.Controller.WardrobePage = first.Page; fixture.Controller.WardrobePage = first.Page;
        fixture.Controller.VersionSettingsPage = settings.Page; fixture.Controller.VersionSettingsPage = settings.Page;
        AssertTrue(fixture.Shell.Tree.IsAlive(first.Page)); AssertTrue(fixture.Shell.Tree.IsAlive(settings.Page));
        Emit(fixture.Intents, "ui.account.wardrobe"); AssertEqual(first.Page, fixture.Shell.Stage.Navigation.Current);
        fixture.Controller.WardrobePage = second.Page;
        AssertEqual(second.Page, fixture.Shell.Stage.Navigation.Current); AssertEqual(2, fixture.Shell.Stage.Navigation.Depth);
        AssertTrue(fixture.Shell.Tree.IsAlive(first.Page));
        bool rejected = false;
        try { fixture.Controller.WardrobePage = default; } catch (ArgumentException) { rejected = true; }
        AssertTrue(rejected); AssertEqual(second.Page, fixture.Shell.Stage.Navigation.Current);
        XsrUiEntityId invalid = fixture.Shell.Tree.Create("NotAPage"); rejected = false;
        try { fixture.Controller.VersionSettingsPage = invalid; } catch (ArgumentException) { rejected = true; }
        AssertTrue(rejected); fixture.Shell.Tree.Destroy(invalid);
        Emit(fixture.Intents, "ui.page.back"); Emit(fixture.Intents, "ui.launch.settings");
        AssertEqual(settings.Page, fixture.Shell.Stage.Navigation.Current);
        fixture.Controller.Dispose(); AssertTrue(fixture.Shell.Tree.IsAlive(settings.Page)); AssertTrue(fixture.Shell.Tree.IsAlive(second.Page));
    }
}
