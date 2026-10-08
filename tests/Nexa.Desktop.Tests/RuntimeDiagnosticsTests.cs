using System.Globalization;
using Nexa.Desktop.Ui;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void DeveloperRuntimeDiagnosticsCaptureActualSceneAndRemainReadOnly()
    {
        using var fixture = new DeveloperDiagnosticsFixture();
        fixture.Select("advanced"); fixture.SetDeveloper(true); fixture.Select("appearance");
        AssertTrue(fixture.Has("SettingsRuntime.Renderer"));
        AssertTrue(fixture.Has("SettingsRuntime.LayoutPaint"));
        AssertTrue(fixture.Has("SettingsRuntime.Scheduler"));
        AssertTrue(fixture.Has("SettingsRuntime.Performance"));
        long revision = fixture.Host.SettingsPolicy.Read(new()).Value!.Revision;
        fixture.Store.Publish(fixture.Store.Resolve(XsrSemanticId.Parse("AAADiagnostic095")), new DeveloperDiagnosticSecret());
        var expected = SettingsPageController.CaptureRuntimeDiagnostics(fixture.Shell);
        long scene = fixture.Shell.Renderer.SceneVersion;
        AssertEqual(scene, expected.Renderer.SceneVersion);
        AssertTrue(expected.Renderer.SceneNodes > 0 && expected.Renderer.TreeEntities >= expected.Renderer.SceneNodes);
        AssertEqual(fixture.Shell.Renderer.LastLayoutVisits, expected.Renderer.LayoutVisits);
        AssertTrue(expected.Renderer.PendingStateEntries > 0);
        AssertEqual(scene, fixture.Shell.Renderer.SceneVersion); // Capture never renders or drains state.
        fixture.Click("ui.settings.diagnostics.runtime.refresh", "SettingsRuntime.Renderer.Refresh");
        AssertEqual(expected.Renderer.SceneVersion.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsRuntime.Renderer.SceneVersion.Value"));
        AssertEqual(expected.Renderer.SceneNodes.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsRuntime.Renderer.SceneNodes.Value"));
        AssertEqual(expected.Renderer.LayoutVisits.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsRuntime.LayoutPaint.LayoutVisits.Value"));
        AssertEqual(expected.Renderer.PendingStateEntries.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsRuntime.Scheduler.PendingState.Value"));
        AssertEqual(expected.Native.RasterAdmissionBudgetBytes.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsRuntime.Performance.AdmissionBudget.Value"));
        string retainedScene = fixture.Text("SettingsRuntime.Renderer.SceneVersion.Value");
        fixture.Pump(); fixture.Pump();
        AssertEqual(retainedScene, fixture.Text("SettingsRuntime.Renderer.SceneVersion.Value"));
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
        fixture.Shell.Tree.Walk(fixture.Find("SettingsRuntime.Performance"), entity =>
        {
            AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiTextInput>(entity) is null);
            return true;
        });
        var retired = fixture.Find("SettingsRuntime.Renderer.Refresh");
        fixture.Select("advanced");
        Emit(fixture.Intents, "ui.settings.diagnostics.runtime.refresh", retired); fixture.Pump();
        AssertFalse(fixture.Has("SettingsRuntime.Renderer"));
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
        fixture.SetDeveloper(false); fixture.Select("appearance");
        AssertFalse(fixture.Has("SettingsRuntime.Renderer"));
        AssertEqual(0, fixture.Secret.FormatCalls);
    }
}
