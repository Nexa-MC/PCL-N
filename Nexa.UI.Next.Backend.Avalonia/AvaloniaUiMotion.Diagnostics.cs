using System.Diagnostics;

namespace Nexa.UI.Next.Backend.Avalonia;

internal static partial class AvaloniaUiMotion
{
    private static readonly long[] RecentTicks = new long[512];
    private static int _recentTickCursor, _recentTickCount;
    internal static void RecordDiagnosticTick()
    {
        // Called while the scheduler's existing Gate is held; this creates no extra timer.
        RecentTicks[_recentTickCursor] = Stopwatch.GetTimestamp();
        _recentTickCursor = (_recentTickCursor + 1) % RecentTicks.Length;
        _recentTickCount = Math.Min(RecentTicks.Length, _recentTickCount + 1);
    }
    internal static (int ActiveTracks, bool TimerRunning, int TargetFramesPerSecond, double? TickRate) CaptureDiagnostics()
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp(), oldest = 0, newest = 0;
            int count = 0;
            for (int offset = 0; offset < _recentTickCount; offset++)
            {
                int index = (_recentTickCursor - 1 - offset + RecentTicks.Length) % RecentTicks.Length;
                long tick = RecentTicks[index];
                if (Stopwatch.GetElapsedTime(tick, now) > TimeSpan.FromSeconds(2)) break;
                if (count == 0) newest = tick;
                oldest = tick; count++;
            }
            double seconds = count > 1 ? Stopwatch.GetElapsedTime(oldest, newest).TotalSeconds : 0;
            return (Active.Count, _timer?.IsEnabled == true, _framesPerSecond, seconds > 0 ? (count - 1) / seconds : null);
        }
    }
}
