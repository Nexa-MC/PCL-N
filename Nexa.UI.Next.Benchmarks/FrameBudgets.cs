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
        Measure("paint-10000-materialized", () => { largeTree.MarkDirty(largeLeaf, XsrUiDirtyKinds.Paint); return large.Render(); });

        foreach (FrameMeasurement result in results)
        {
            Report(string.Create(CultureInfo.InvariantCulture,
                $"{result.Name}: P50={result.P50:F3}ms P95={result.P95:F3}ms P99={result.P99:F3}ms; {result.Frames} frames"));
            if (timingGate is not null)
            {
                bool passes = timingGate == "120hz"
                    ? result.P50 < 2 && result.P95 < 5 && result.P99 < 8
                    : result.P99 < 16.6;
                Gate(passes, result.Name, $"controlled runner {timingGate} kernel budget");
            }
        }
        if (output is null) return;
        // Utf8JsonWriter keeps this executable usable in NativeAOT without reflection serialization.
        using FileStream file = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter json = new(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schema", 1);
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
            json.WriteNumber("allocated_bytes", result.Allocated);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        void Measure(string name, Func<XsrUiScene> frame)
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
            results.Add(new(name, frames, entities, allocated, elapsed[499], elapsed[949], elapsed[989]));
        }
    }

    private sealed record FrameMeasurement(string Name, int Frames, int Entities, long Allocated, double P50, double P95, double P99);
}
