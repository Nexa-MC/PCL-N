using Avalonia;
using Avalonia.Media.Imaging;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void RuntimeDiagnosticsObserveRealCommitsPaintAndAdmission()
    {
        var shell = XsrUiShellComposer.Compose(new XsrStateStoreBuilder().Build());
        shell.Renderer.ReducedMotion = true;
        using var surface = new AvaloniaUiSceneSurface(shell);
        surface.Measure(new(800, 600)); surface.Arrange(new(0, 0, 800, 600));
        var before = AvaloniaUiRuntimeDiagnostics.Capture();
        surface.CommitScene();
        var committed = AvaloniaUiRuntimeDiagnostics.Capture();
        AssertTrue(committed.CommittedScenes > before.CommittedScenes);
        surface.CommitScene();
        AssertEqual(committed.CommittedScenes, AvaloniaUiRuntimeDiagnostics.Capture().CommittedScenes);
        shell.Tree.MarkDirty(shell.Root, XsrUiDirtyKinds.Paint); surface.CommitScene();
        AssertTrue(AvaloniaUiRuntimeDiagnostics.Capture().CommittedScenes > committed.CommittedScenes);

        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        control.Apply(Node(shell.Root, XsrUiSemanticRole.None));
        control.Measure(new(16, 16)); control.Arrange(new(0, 0, 16, 16));
        before = AvaloniaUiRuntimeDiagnostics.Capture();
        using (var target = new RenderTargetBitmap(new PixelSize(16, 16))) target.Render(control);
        var painted = AvaloniaUiRuntimeDiagnostics.Capture();
        AssertTrue(painted.NodePaintCallbacks > before.NodePaintCallbacks);
        AssertEqual(painted.NodePaintCallbacks, AvaloniaUiRuntimeDiagnostics.Capture().NodePaintCallbacks);

        var shared = AvaloniaUiRasterPool.Shared.CaptureDiagnostics();
        AssertEqual(shared.Bytes, painted.RasterPixelChargeBytes);
        AssertEqual(shared.AdmissionBudget, painted.RasterAdmissionBudgetBytes);
        AssertEqual(shared.Leases, painted.RasterLeases);
        AssertEqual(shared.Pressure, painted.MemoryPressure);
        object owner = new(); double value = 0;
        before = AvaloniaUiRuntimeDiagnostics.Capture();
        try
        {
            AvaloniaUiMotion.Animate(owner, owner, () => value, updated => value = updated, 1, 1000);
            AssertEqual(before.ActiveMotionTracks + 1, AvaloniaUiRuntimeDiagnostics.Capture().ActiveMotionTracks);
        }
        finally { AvaloniaUiMotion.CancelAll(owner); }
        AssertEqual(before.ActiveMotionTracks, AvaloniaUiRuntimeDiagnostics.Capture().ActiveMotionTracks);
    }
}
