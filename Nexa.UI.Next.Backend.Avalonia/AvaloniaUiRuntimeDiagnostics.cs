namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Native process-wide submission and admission facts; no physical display FPS claim.</summary>
public readonly record struct AvaloniaUiRuntimeCapture(long CommittedScenes, long NodePaintCallbacks,
    int ActiveMotionTracks, bool MotionTimerRunning, int MotionTargetFramesPerSecond, double? MotionTickRate,
    long RasterPixelChargeBytes, long RasterAdmissionBudgetBytes, int RasterEntries, int RasterLeases,
    long RasterDecodeAttempts, long RasterDisposedBitmaps, AvaloniaUiMemoryObservation MemoryPressure);

public static class AvaloniaUiRuntimeDiagnostics
{
    private static long _committedScenes, _nodePaintCallbacks;
    internal static void SceneCommitted() => Interlocked.Increment(ref _committedScenes);
    internal static void NodePainted() => Interlocked.Increment(ref _nodePaintCallbacks);

    /// <summary>Copies only counters and the latest sample; it performs no native I/O or render.</summary>
    public static AvaloniaUiRuntimeCapture Capture()
    {
        var motion = AvaloniaUiMotion.CaptureDiagnostics();
        var raster = AvaloniaUiRasterPool.Shared.CaptureDiagnostics();
        return new(Interlocked.Read(ref _committedScenes), Interlocked.Read(ref _nodePaintCallbacks),
            motion.ActiveTracks, motion.TimerRunning, motion.TargetFramesPerSecond, motion.TickRate,
            raster.Bytes, raster.AdmissionBudget, raster.Entries, raster.Leases, raster.DecodeAttempts,
            raster.DisposedBitmaps, raster.Pressure);
    }
}
