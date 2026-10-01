using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nexa.Desktop.Ui;
using Nexa.Services.Logging;
using Nexa.Services.Tasks;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static int RunDesktopSoak(string[] args)
    {
        string? mode = null, output = null;
        int seconds = 0;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (++index >= args.Length) return 2;
            if (option == "--soak" && mode is null && args[index] is "idle" or "navigation") mode = args[index];
            else if (option == "--seconds" && seconds == 0 && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is >= 1 and <= 14400) seconds = value;
            else if (option == "--output" && output is null) output = args[index];
            else return 2;
        }
        if (mode is null || output is null || seconds == 0 || Path.Exists(output))
        {
            Console.Error.WriteLine("Usage: --soak idle|navigation --seconds 1..14400 --output NEW-DIRECTORY");
            return 2;
        }
        Directory.CreateDirectory(output);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        using SettingsPageController settings = new(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Controller.SettingsPage = settings.Page;
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        long[] frameHistogram = new long[1001];
        // Materialize every exercised page before measuring retention; cache fill is not a leak.
        for (int warm = 0; warm < 100; warm++) Frame(warm);
        Array.Clear(frameHistogram);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process process = Process.GetCurrentProcess();
        using FileStream samples = new(Path.Combine(output, "samples.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        long allocated = GC.GetTotalAllocatedBytes();
        long start = Stopwatch.GetTimestamp();
        SoakSample baseline = Capture(0);
        WriteSample(samples, baseline);
        int frame = 0, sampleCount = 1;
        double nextSample = 1;
        SoakSample peak = baseline;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            Frame(frame++);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (elapsed >= nextSample)
            {
                SoakSample sample = Capture(elapsed);
                WriteSample(samples, sample);
                sampleCount++;
                peak = peak with { Entities = Math.Max(peak.Entities, sample.Entities), Logs = Math.Max(peak.Logs, sample.Logs), Tasks = Math.Max(peak.Tasks, sample.Tasks) };
                nextSample = elapsed + 1;
            }
            Thread.Sleep(16);
        }
        // Compare collected live bytes, not the current size of the allocation nursery.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SoakSample final = Capture(Stopwatch.GetElapsedTime(start).TotalSeconds);
        WriteSample(samples, final);
        samples.Flush(true);
        bool passed = final.LiveBytes - baseline.LiveBytes <= 16 * 1024 * 1024
            && final.WorkingSet - baseline.WorkingSet <= 128 * 1024 * 1024
            && final.Threads - baseline.Threads <= 16
            && (final.Handles is null || baseline.Handles is null || final.Handles - baseline.Handles <= 16)
            && final.Entities <= baseline.Entities + 128 && peak.Entities <= baseline.Entities + 256
            && final.States == baseline.States && peak.Logs <= fixture.Foundation.Host.Logging.Capacity && peak.Tasks <= 30;
        using FileStream report = new(Path.Combine(output, "run.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter json = new(report, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", 1);
        json.WriteString("scope", "desktop-composition-fixture");
        json.WriteString("mode", mode);
        json.WriteNumber("requested_seconds", seconds);
        json.WriteNumber("elapsed_seconds", final.Seconds);
        json.WriteNumber("frames", frame);
        json.WriteNumber("samples", sampleCount + 1);
        json.WriteNumber("composition_frame_p50_ms", Percentile(.50));
        json.WriteNumber("composition_frame_p95_ms", Percentile(.95));
        json.WriteNumber("composition_frame_p99_ms", Percentile(.99));
        json.WriteString("timing_scope", "intent-plus-render; 0.1ms histogram upper bounds; overflow at 100ms; no OS backend");
        json.WriteBoolean("two_hour_fixture_soak", final.Seconds >= 7200);
        json.WriteBoolean("real_desktop_acceptance", false);
        json.WriteBoolean("passed", passed);
        json.WriteStartArray("unmeasured");
        foreach (string item in new[] { "os-window", "gpu-frame", "sidecar-sessions", "jvm-native-memory", "network-pool", "resource-cache", "recovery-blobs", "file-watchers", "real-install-launch" }) json.WriteStringValue(item);
        json.WriteEndArray();
        json.WriteEndObject();
        Console.WriteLine($"Desktop {mode} fixture soak: {final.Seconds:F1}s / {frame} frames; {(passed ? "PASS" : "FAIL")}. Real desktop acceptance remains pending.");
        return passed ? 0 : 1;

        void Frame(int index)
        {
            long started = Stopwatch.GetTimestamp();
            if (mode == "navigation")
            {
                string route = (index % 3) switch { 0 => "ui.navigation.settings", 1 => "ui.navigation.download", _ => "ui.navigation.launch" };
                Emit(fixture.Intents, route);
            }
            _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
            int bucket = Math.Min(1000, (int)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 10));
            frameHistogram[bucket]++;
        }

        double Percentile(double percentile)
        {
            long target = (long)Math.Ceiling(frame * percentile), count = 0;
            for (int bucket = 0; bucket < frameHistogram.Length; bucket++)
            {
                count += frameHistogram[bucket];
                if (count >= target) return bucket / 10d;
            }
            return 100;
        }

        SoakSample Capture(double elapsed)
        {
            process.Refresh();
            int? handles;
            try { handles = process.HandleCount; }
            catch (PlatformNotSupportedException) { handles = null; }
            return new(elapsed, process.WorkingSet64, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes() - allocated,
                handles, process.Threads.Count, fixture.Shell.Tree.Count, fixture.Store.Count,
                fixture.Foundation.Host.Logging.GetSnapshot().Count,
                fixture.Store.ReadCollection<TaskCenterEntry>(fixture.Store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Count(task => task.IsTerminal));
        }
    }

    private static void WriteSample(Stream stream, SoakSample sample)
    {
        using (Utf8JsonWriter json = new(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("seconds", sample.Seconds);
            json.WriteNumber("working_set_bytes", sample.WorkingSet);
            json.WriteNumber("managed_live_bytes", sample.LiveBytes);
            json.WriteNumber("allocated_bytes", sample.Allocated);
            if (sample.Handles is { } handles) json.WriteNumber("handles", handles); else json.WriteNull("handles");
            json.WriteNumber("threads", sample.Threads);
            json.WriteNumber("scene_entities", sample.Entities);
            json.WriteNumber("state_cells", sample.States);
            json.WriteNumber("log_entries", sample.Logs);
            json.WriteNumber("terminal_tasks", sample.Tasks);
            json.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
    }

    private sealed record SoakSample(double Seconds, long WorkingSet, long LiveBytes, long Allocated,
        int? Handles, int Threads, int Entities, int States, int Logs, int Tasks);
}
