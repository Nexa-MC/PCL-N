using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Benchmarks;

internal static partial class Program
{
    private static bool TryReadOptions(string[] args, out string? output, out string? timingGate)
    {
        output = null;
        timingGate = null;
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (++index >= args.Length || option is not ("--output" or "--timing-gate"))
            {
                Console.Error.WriteLine("Usage: Nexa.UI.Next.Benchmarks [--output FILE] [--timing-gate 120hz|60hz]");
                return false;
            }
            if (option == "--output" && output is null) output = args[index];
            else if (option == "--timing-gate" && timingGate is null && args[index] is "120hz" or "60hz") timingGate = args[index];
            else return false;
        }
        return true;
    }

    private static void RunPercentileReport(string? output, string? timingGate)
    {
        XsrUiRenderer renderer = BuildGridRenderer(40, 40, out XsrUiTree tree, out _);
        XsrUiEntityId leaf = FindFirstLeaf(tree, out _);
        XsrUiElement element = tree.GetComponent<XsrUiElement>(leaf)!;
        var results = new List<FrameMeasurement>();
        Measure("clean-1600", () => renderer.Render());
        Measure("paint-1600", () => { tree.MarkDirty(leaf, XsrUiDirtyKinds.Paint); return renderer.Render(); });
        Measure("layout-1600", () =>
        {
            element.Height = element.Height == 20 ? 21 : 20;
            tree.MarkDirty(leaf, XsrUiDirtyKinds.Layout);
            return renderer.Render();
        });
        XsrUiRenderer large = BuildGridRenderer(100, 100, out XsrUiTree largeTree, out _);
        XsrUiEntityId largeLeaf = FindFirstLeaf(largeTree, out _);
        Measure("paint-10000-materialized", () => { largeTree.MarkDirty(largeLeaf, XsrUiDirtyKinds.Paint); return large.Render(); }, routine: false);

        foreach (FrameMeasurement result in results)
        {
            Report(string.Create(CultureInfo.InvariantCulture,
                $"{result.Name}: P50={result.P50:F3}ms P95={result.P95:F3}ms P99={result.P99:F3}ms P99.9={result.P999:F3}ms max={result.Maximum:F3}ms; {result.Frames} frames"));
            if (timingGate is not null && result.Routine)
            {
                bool passes = timingGate == "120hz"
                    ? result.P50 < 2 && result.P95 < 3 && result.P99 < 6
                    : result.P99 < 16.6;
                Gate(passes, result.Name, $"controlled runner {timingGate} kernel budget");
            }
        }
        if (output is null) return;
        // Utf8JsonWriter keeps this executable usable in NativeAOT without reflection serialization.
        using FileStream file = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter json = new(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", 2);
        json.WriteString("scope", "renderer-kernel-only");
        json.WriteString("framework", RuntimeInformation.FrameworkDescription);
        json.WriteString("os", RuntimeInformation.OSDescription);
        json.WriteString("arch", RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteString("timing_gate", timingGate);
        json.WriteBoolean("passed", _failures == 0);
        json.WriteStartArray("scenarios");
        foreach (FrameMeasurement result in results)
        {
            json.WriteStartObject();
            json.WriteString("name", result.Name);
            json.WriteNumber("frames", result.Frames);
            json.WriteNumber("scene_entities", result.Entities);
            json.WriteNumber("p50_ms", result.P50);
            json.WriteNumber("p95_ms", result.P95);
            json.WriteNumber("p99_ms", result.P99);
            json.WriteNumber("p999_ms", result.P999);
            json.WriteNumber("max_ms", result.Maximum);
            json.WriteNumber("worst_1_percent_mean_ms", result.WorstOnePercent);
            json.WriteNumber("worst_01_percent_mean_ms", result.WorstPointOnePercent);
            json.WriteNumber("worst_1_percent_samples", TailCount(result.Frames, .01));
            json.WriteNumber("worst_01_percent_samples", TailCount(result.Frames, .001));
            json.WriteBoolean("routine_timing_gate_eligible", result.Routine);
            json.WriteNumber("allocated_bytes", result.Allocated);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        void Measure(string name, Func<XsrUiScene> frame, bool routine = true)
        {
            const int frames = 1000;
            for (int warm = 0; warm < 100; warm++) _ = frame();
            double[] elapsed = new double[frames];
            long before = GC.GetAllocatedBytesForCurrentThread();
            int entities = 0;
            for (int index = 0; index < frames; index++)
            {
                long start = Stopwatch.GetTimestamp();
                entities = frame().Count;
                elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(elapsed);
            results.Add(new(name, frames, entities, allocated, elapsed[499], elapsed[949], elapsed[989], elapsed[998],
                elapsed[^1], TailMean(elapsed, .01), TailMean(elapsed, .001), routine));
        }
    }

    private static int TailCount(int samples, double fraction) => Math.Max(1, (int)Math.Ceiling(samples * fraction));

    private static double TailMean(double[] sorted, double fraction)
    {
        int count = TailCount(sorted.Length, fraction);
        double total = 0;
        for (int index = sorted.Length - count; index < sorted.Length; index++) total += sorted[index];
        return total / count;
    }

    private static void CheckTailStatistics()
    {
        double[] samples = new double[1000];
        for (int index = 0; index < samples.Length; index++) samples[index] = index + 1;
        Gate(TailCount(samples.Length, .01) == 10 && TailMean(samples, .01) == 995.5
            && TailCount(samples.Length, .001) == 1 && TailMean(samples, .001) == 1000,
            "tail sample counts and means", "tail metrics use the slowest observations, with explicit sample resolution");
    }

    private sealed record FrameMeasurement(string Name, int Frames, int Entities, long Allocated,
        double P50, double P95, double P99, double P999, double Maximum, double WorstOnePercent,
        double WorstPointOnePercent, bool Routine);
}
