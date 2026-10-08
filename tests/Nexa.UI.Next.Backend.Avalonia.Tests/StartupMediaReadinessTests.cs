using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void StartupMediaWarmupSettlesMissingLocalAsset()
    {
        static async Task CheckAsync()
        {
            string absent = Path.Combine(Path.GetTempPath(), "nexa-missing-media-" + Guid.NewGuid().ToString("N") + ".mp4");
            await using var video = new AvaloniaUiLocalMediaPlayer(video: true);
            await video.ConfigureAsync(absent, 0, play: false);
            AssertEqual(AvaloniaUiMediaStatus.Paused, video.State.Status);
            AssertTrue(await video.WarmUpAsync() is null);
            AssertEqual(AvaloniaUiMediaStatus.Failed, video.State.Status);
            AssertEqual("Local media file is missing.", video.State.Detail);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool stopped = false;
            try { await video.WarmUpAsync(canceled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            AssertTrue(stopped);
        }
        CheckAsync().GetAwaiter().GetResult();
    }
}
