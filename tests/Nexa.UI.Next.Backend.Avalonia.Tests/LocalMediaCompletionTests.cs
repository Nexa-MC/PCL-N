using System.Buffers.Binary;
using System.Diagnostics;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void MediaFramesRejectUnboundedInputAndOwnedDecoderPauses()
        => MediaFramesRejectUnboundedInputAndOwnedDecoderPausesAsync().GetAwaiter().GetResult();

    private static async Task MediaFramesRejectUnboundedInputAndOwnedDecoderPausesAsync()
    {
        byte[] oversized = new byte[16];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(oversized, 0);
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(8), uint.MaxValue);
        bool rejected = false;
        try { await AvaloniaUiLocalMediaPlayer.ReadFrameAsync(new MemoryStream(oversized), default); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
        AssertTrue(await AvaloniaUiLocalMediaPlayer.ReadFrameAsync(new MemoryStream(), default) is null);
        string directory = Path.Combine(Path.GetTempPath(), "nexa-local-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "local video.mp4");
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64:d=2", "-c:v", "mpeg4", "-threads", "1", "-y", path }) start.ArgumentList.Add(argument);
            Process? generator;
            try { generator = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Optional ffmpeg decoder smoke: DependencyMissing."); return; }
            using (generator)
            {
                Task<string> stderr = generator!.StandardError.ReadToEndAsync();
                await generator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                string errors = await stderr;
                if (generator.ExitCode != 0) throw new InvalidOperationException(errors);
            }
            var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var player = new AvaloniaUiLocalMediaPlayer(true, image =>
            { AssertTrue(image.Width <= 960); AssertTrue(image.Height <= 540); firstFrame.TrySetResult(true); });
            await player.ConfigureAsync(path, 0, true);
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertEqual(AvaloniaUiMediaStatus.Playing, player.State.Status);
            await player.ConfigureAsync(path, 0, false);
            AssertEqual(AvaloniaUiMediaStatus.Paused, player.State.Status);
            await player.ConfigureAsync("", 0, false);
            AssertEqual(AvaloniaUiMediaStatus.Stopped, player.State.Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MprisNativeBusPropertiesAndControlsUseRealAdapter()
        => MprisNativeBusPropertiesAndControlsUseRealAdapterAsync().GetAwaiter().GetResult();
    private static async Task MprisNativeBusPropertiesAndControlsUseRealAdapterAsync()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        { Console.WriteLine("MPRIS native smoke requires dbus-run-session."); return; }
        var invoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        double savedVolume = -1;
        var errors = new List<string>();
        await using var session = new AvaloniaUiMprisSession(() => new(AvaloniaUiMediaStatus.Playing), () => .37,
            (command, value) => { if (value is { } volume) savedVolume = volume; else invoked.TrySetResult(command); },
            message => { lock (errors) errors.Add(message); });
        string[] callPrefix = ["call", "--session", "--dest", "org.mpris.MediaPlayer2.nexacl", "--object-path", "/org/mpris/MediaPlayer2", "--timeout", "3", "--method"];
        async Task<(int ExitCode, string Output)> Call(params string[] tail)
        {
            var start = new ProcessStartInfo("gdbus") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in callPrefix.Concat(tail)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return (process.ExitCode, await stdout + await stderr);
        }
        (int ExitCode, string Output) playback = (-1, "");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            playback = await Call("org.freedesktop.DBus.Properties.Get", "org.mpris.MediaPlayer2.Player", "PlaybackStatus");
            if (playback.ExitCode == 0) break;
            await Task.Delay(50);
        }
        AssertEqual(0, playback.ExitCode); AssertTrue(playback.Output.Contains("Playing", StringComparison.Ordinal));
        var all = await Call("org.freedesktop.DBus.Properties.GetAll", "org.mpris.MediaPlayer2.Player");
        AssertEqual(0, all.ExitCode); AssertTrue(all.Output.Contains("CanSeek", StringComparison.Ordinal));
        AssertEqual(0, (await Call("org.mpris.MediaPlayer2.Player.PlayPause")).ExitCode);
        AssertEqual("play-pause", await invoked.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        AssertEqual(0, (await Call("org.freedesktop.DBus.Properties.Set", "org.mpris.MediaPlayer2.Player", "Volume", "<0.6>")).ExitCode);
        AssertEqual(.6, savedVolume);
        lock (errors) AssertEqual(0, errors.Count);
    }
}
