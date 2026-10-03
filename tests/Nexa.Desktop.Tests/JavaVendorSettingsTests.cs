using Nexa.Desktop.Ui;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void JavaVendorSelectorFitsAndScrollsInNarrowWindow()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        fixture.Shell.Renderer.ReducedMotion = true;
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(700, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.java").Entity);
        scene = fixture.Shell.Render(new(700, 650));
        var body = FindByKey(fixture.Shell, scene, "SettingsSections");
        var selector = FindByKey(fixture.Shell, scene, "SettingsSelector.java.vendor");
        AssertTrue(selector.Rect.Width < 400);
        AssertTrue(selector.Rect.X + selector.Rect.Width <= body.Rect.X + body.Rect.Width);
        AssertTrue(selector.Scroll!.Value.ContentWidth > selector.Scroll.Value.ViewportWidth);
        var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(selector.Entity)!;
        scroll.OffsetX = 10000;
        fixture.Shell.Tree.MarkDirty(selector.Entity, XsrUiDirtyKinds.Layout);
        scene = fixture.Shell.Render(new(700, 650));
        var last = FindByKey(fixture.Shell, scene, "SettingsOption.java.vendor.Unknown");
        AssertTrue(last.Rect.X + last.Rect.Width <= selector.Rect.X + selector.Rect.Width + .01);
        var point = new XsrUiPoint(last.Rect.X + last.Rect.Width / 2, last.Rect.Y + last.Rect.Height / 2);
        AssertTrue(fixture.Shell.Renderer.PointerPressed(point));
        AssertTrue(fixture.Shell.Renderer.PointerReleased(point));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(700, 650));
            return fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "java.vendor").Value.Value == "Unknown";
        }, TimeSpan.FromSeconds(5)));
    }
}
