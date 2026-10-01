using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nexa.Desktop.Ui;
using Nexa.Services.Logging;
using Nexa.Services.Tasks;
using Nexa.Services.Scheduling;
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
            else if (option == "--seconds" && seconds == 0 && int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value is >= 1 and <= 28800) seconds = value;
            else if (option == "--output" && output is null) output = args[index];
            else return 2;
        }
        if (mode is null || output is null || seconds == 0 || Path.Exists(output))
        {
            Console.Error.WriteLine("Usage: --soak idle|navigation --seconds 1..28800 --output NEW-DIRECTORY");
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
        double frameMax = 0;
        // Materialize every exercised page before measuring retention; cache fill is not a leak.
        for (int warm = 0; warm < 100; warm++) Frame(warm);
        Array.Clear(frameHistogram);
        frameMax = 0;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using SoakFrameRequests requests = new(fixture.Shell);
        using Process process = Process.GetCurrentProcess();
        using FileStream samples = new(Path.Combine(output, "samples.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        long allocated = GC.GetTotalAllocatedBytes();
        long start = Stopwatch.GetTimestamp();
        SoakSample baseline = Capture(0);
        TimeSpan baselineCpu = process.TotalProcessorTime;
        WriteSample(samples, baseline);
        int frame = 0, sampleCount = 1;
        double nextSample = 1;
        SoakSample peak = baseline;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
        {
            if (mode == "navigation" || requests.TryTake()) Frame(frame++);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (elapsed >= nextSample)
            {
                SoakSample sample = Capture(elapsed);
                WriteSample(samples, sample);
                sampleCount++;
                peak = peak with
                {
                    WorkingSet = Math.Max(peak.WorkingSet, sample.WorkingSet),
                    PrivateBytes = Math.Max(peak.PrivateBytes, sample.PrivateBytes),
                    LiveBytes = Math.Max(peak.LiveBytes, sample.LiveBytes),
                    Entities = Math.Max(peak.Entities, sample.Entities),
                    Logs = Math.Max(peak.Logs, sample.Logs),
                    Tasks = Math.Max(peak.Tasks, sample.Tasks)
                };
                nextSample = elapsed + 1;
            }
            if (mode == "navigation") Thread.Sleep(16);
            else requests.Wait(TimeSpan.FromSeconds(Math.Clamp(Math.Min(nextSample, seconds) - elapsed, 0, 1)));
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
            && final.PrivateBytes - baseline.PrivateBytes <= 128 * 1024 * 1024
            && final.Threads - baseline.Threads <= 16
            && (final.Handles is null || baseline.Handles is null || final.Handles - baseline.Handles <= 16)
            && final.Entities <= baseline.Entities + 128 && peak.Entities <= baseline.Entities + 256
            && final.States == baseline.States && peak.Logs <= fixture.Foundation.Host.Logging.Capacity && peak.Tasks <= 30
            && final.Work.QuietScopes == 0 && final.Work.Resources.All(r => r.Active == 0 && r.Waiting == 0);
        using FileStream report = new(Path.Combine(output, "run.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter json = new(report, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", 2);
        json.WriteString("scope", "desktop-composition-fixture");
        json.WriteString("mode", mode);
        json.WriteNumber("requested_seconds", seconds);
        json.WriteNumber("elapsed_seconds", final.Seconds);
        json.WriteNumber("frames", frame);
        json.WriteNumber("samples", sampleCount + 1);
        WriteNullableNumber(json, "composition_frame_p50_ms", Percentile(.50));
        WriteNullableNumber(json, "composition_frame_p95_ms", Percentile(.95));
        WriteNullableNumber(json, "composition_frame_p99_ms", Percentile(.99));
        WriteNullableNumber(json, "composition_frame_p999_ms", Percentile(.999));
        WriteNullableNumber(json, "composition_frame_max_ms", frame == 0 ? null : frameMax);
        json.WriteNumber("frame_histogram_overflow_count", frameHistogram[1000]);
        json.WriteNumber("render_requests", requests.Count);
        json.WriteString("frame_driver", mode == "idle" ? "tree-state-invalidation-coalesced" : "navigation-workload");
        json.WriteNumber("runtime_processor_count", Environment.ProcessorCount);
        json.WriteString("processor_count_scope", "Environment.ProcessorCount respects affinity, quota and DOTNET_PROCESSOR_COUNT; not necessarily whole-machine CPU capacity");
        double cpuMs = (process.TotalProcessorTime - baselineCpu).TotalMilliseconds;
        json.WriteNumber("process_cpu_ms", cpuMs);
        json.WriteNumber("cpu_percent_one_core", cpuMs / (final.Seconds * 10));
        json.WriteNumber("cpu_percent_runtime_normalized", cpuMs / (final.Seconds * 10 * Environment.ProcessorCount));
        json.WriteNumber("peak_working_set_bytes", Math.Max(peak.WorkingSet, final.WorkingSet));
        json.WriteNumber("peak_private_bytes", Math.Max(peak.PrivateBytes, final.PrivateBytes));
        json.WriteNumber("peak_managed_live_bytes", Math.Max(peak.LiveBytes, final.LiveBytes));
        json.WriteString("scheduler_scope", "host admission leases and queues; not OS HTTP connections or disk I/O counters");
        json.WriteString("managed_live_bytes_scope", "GC.GetTotalMemory(false) estimate; baseline and final after explicit full GC");
        json.WriteString("allocation_scope", "process total includes fixture, one-second Process/JSON observer and explicit final GC; not product idle allocation");
        json.WriteString("gate_scope", "bounded fixture endpoint retention; not monotonic-trend or runtime KPI certification");
        json.WriteString("timing_scope", "intent-plus-render; 0.1ms histogram upper bounds below 100ms; saturated percentile is null, max is exact; no OS backend");
        json.WriteBoolean("two_hour_fixture_soak", final.Seconds >= 7200);
        json.WriteBoolean("eight_hour_fixture_soak", final.Seconds >= 28800);
        json.WriteBoolean("real_desktop_acceptance", false);
        json.WriteBoolean("passed", passed);
        json.WriteStartArray("unmeasured");
        foreach (string item in new[] { "os-window", "gpu-frame", "sidecar-sessions", "jvm-native-memory", "network-pool", "resource-cache", "recovery-blobs", "file-watchers", "real-install-launch", "platform-commit", "native-memory", "gpu-textures", "disk-io", "http-connections" }) json.WriteStringValue(item);
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
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            frameMax = Math.Max(frameMax, elapsedMs);
            int bucket = Math.Min(1000, (int)Math.Ceiling(elapsedMs * 10));
            frameHistogram[bucket]++;
        }

        double? Percentile(double percentile)
        {
            if (frame == 0) return null;
            long target = (long)Math.Ceiling(frame * percentile), count = 0;
            for (int bucket = 0; bucket < frameHistogram.Length; bucket++)
            {
                count += frameHistogram[bucket];
                if (count >= target) return bucket == 1000 ? null : bucket / 10d;
            }
            return null;
        }

        SoakSample Capture(double elapsed)
        {
            process.Refresh();
            int? handles;
            try { handles = process.HandleCount; }
            catch (PlatformNotSupportedException) { handles = null; }
            return new(elapsed, process.WorkingSet64, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes() - allocated,
                handles, process.Threads.Count, fixture.Shell.Tree.Count, fixture.Store.Count,
                process.PrivateMemorySize64, GC.GetGCMemoryInfo().TotalCommittedBytes, process.TotalProcessorTime.TotalMilliseconds,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                fixture.Foundation.Host.Logging.GetSnapshot().Count,
                fixture.Store.ReadCollection<TaskCenterEntry>(fixture.Store.Resolve(TaskCenterStateContract.EntriesKey)).Items.Count(task => task.IsTerminal),
                fixture.Foundation.Host.Work.Snapshot);
        }
    }

    private static void SoakIdleDriverCoalescesInvalidationsAndRetires()
    {
        using WidgetClock clock = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), timeProvider: clock);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        fixture.Shell.Renderer.ReducedMotion = true;
        Emit(fixture.Intents, "ui.launch.widget.trivia");
        _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
        using SoakFrameRequests requests = new(fixture.Shell);
        AssertFalse(requests.TryTake());
        AssertFalse(requests.TryTake());
        fixture.Shell.Tree.MarkDirty(fixture.Controller.AccountBody, XsrUiDirtyKinds.Paint);
        fixture.Shell.Tree.MarkDirty(fixture.Controller.AccountBody, XsrUiDirtyKinds.Paint);
        AssertEqual(2L, requests.Count);
        AssertTrue(requests.TryTake());
        AssertFalse(requests.TryTake());
        _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
        for (int drain = 0; drain < 32 && requests.TryTake(); drain++) _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertFalse(requests.TryTake());
        long before = requests.Count;
        // State publications wake the driver even before the tree is updated by a frame.
        Task.Run(() => clock.Advance(TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
        AssertTrue(requests.Count > before);
        AssertTrue(requests.TryTake());
        requests.Dispose();
        long retired = requests.Count;
        fixture.Shell.Tree.MarkDirty(fixture.Controller.AccountBody, XsrUiDirtyKinds.Paint);
        clock.Advance(TimeSpan.FromSeconds(3));
        AssertEqual(retired, requests.Count);
    }

    private static void WriteSample(Stream stream, SoakSample sample)
    {
        using (Utf8JsonWriter json = new(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("seconds", sample.Seconds);
            json.WriteNumber("working_set_bytes", sample.WorkingSet);
            json.WriteNumber("managed_live_bytes", sample.LiveBytes);
            json.WriteNumber("private_bytes", sample.PrivateBytes);
            json.WriteNumber("gc_committed_bytes", sample.GcCommitted);
            json.WriteNumber("process_cpu_ms", sample.CpuMs);
            json.WriteNumber("gc_gen0_count", sample.Gen0);
            json.WriteNumber("gc_gen1_count", sample.Gen1);
            json.WriteNumber("gc_gen2_count", sample.Gen2);
            json.WriteNumber("allocated_bytes", sample.Allocated);
            if (sample.Handles is { } handles) json.WriteNumber("handles", handles); else json.WriteNull("handles");
            json.WriteNumber("threads", sample.Threads);
            json.WriteNumber("scene_entities", sample.Entities);
            json.WriteNumber("state_cells", sample.States);
            json.WriteNumber("log_entries", sample.Logs);
            json.WriteNumber("terminal_tasks", sample.Tasks);
            json.WriteNumber("quiet_scopes", sample.Work.QuietScopes);
            json.WriteStartArray("admission_resources");
            foreach (WorkResourceSnapshot resource in sample.Work.Resources)
            {
                json.WriteStartObject(); json.WriteString("resource", resource.Resource.ToString());
                json.WriteNumber("active", resource.Active); json.WriteNumber("waiting", resource.Waiting); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
    }

    private static void WriteNullableNumber(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number) json.WriteNumber(name, number); else json.WriteNull(name);
    }

    private sealed class SoakFrameRequests : IDisposable
    {
        private readonly XsrUiShell _shell;
        private readonly AutoResetEvent _wake = new(false);
        private readonly object _gate = new();
        private bool _disposed;
        private int _pending;
        private long _count;
        public SoakFrameRequests(XsrUiShell shell)
        {
            _shell = shell;
            shell.Tree.RenderInvalidated += Request;
            if (shell.StateBridge is { } bridge) bridge.RenderRequested += Request;
        }
        public long Count => Interlocked.Read(ref _count);
        public bool TryTake() => Interlocked.Exchange(ref _pending, 0) != 0;
        public void Wait(TimeSpan timeout) => _wake.WaitOne(timeout);
        private void Request(object? sender, EventArgs args)
        {
            lock (_gate)
            {
                if (_disposed) return;
                Interlocked.Increment(ref _count);
                if (Interlocked.Exchange(ref _pending, 1) == 0) _wake.Set();
            }
        }
        public void Dispose()
        {
            _shell.Tree.RenderInvalidated -= Request;
            if (_shell.StateBridge is { } bridge) bridge.RenderRequested -= Request;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _wake.Dispose();
            }
        }
    }

    private sealed record SoakSample(double Seconds, long WorkingSet, long LiveBytes, long Allocated,
        int? Handles, int Threads, int Entities, int States, long PrivateBytes, long GcCommitted, double CpuMs,
        int Gen0, int Gen1, int Gen2, int Logs, int Tasks, WorkSchedulerSnapshot Work);
}
