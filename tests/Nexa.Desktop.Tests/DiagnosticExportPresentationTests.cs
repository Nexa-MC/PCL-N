using Nexa.Desktop.Ui;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void DiagnosticExportRequiresClickCoalescesRequestsAndCancelsOnDispose()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        CancellationToken observed = default;
        settings.ExportDiagnostics = token => { requests++; observed = token; return done.Task; };
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.about").Entity);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual(0, requests);
        var sections = FindByKey(fixture.Shell, scene, "SettingsSections").Entity;
        fixture.Shell.Tree.GetComponent<XsrUiScroll>(sections)!.OffsetY = 10000;
        fixture.Shell.Tree.MarkDirty(sections, XsrUiDirtyKinds.Layout);
        scene = fixture.Shell.Render(new(1000, 650));
        var button = FindByKey(fixture.Shell, scene, "Management.导出诊断包").Entity;
        Emit(fixture.Intents, "ui.settings.management.action", button);
        fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.management.action", button);
        fixture.Shell.Render(new(1000, 650));
        AssertEqual(1, requests);
        settings.Dispose();
        AssertTrue(observed.IsCancellationRequested);
        done.SetResult(true);
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains("已导出", StringComparison.Ordinal)));
    }
}
