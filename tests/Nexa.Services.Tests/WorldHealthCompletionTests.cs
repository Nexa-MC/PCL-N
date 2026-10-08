using System.Buffers.Binary;
using System.IO.Compression;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static byte[] HealthChunkFixture(byte compression, byte[] nbt)
    {
        if (compression == 3) return nbt;
        using var output = new MemoryStream();
        using (Stream encoder = compression == 1 ? new GZipStream(output, CompressionLevel.Optimal, true) : new ZLibStream(output, CompressionLevel.Optimal, true)) encoder.Write(nbt);
        return output.ToArray();
    }
    private static byte[] HealthRegionFixture(byte compression, byte[] payload)
    {
        byte[] region = new byte[3 * 4096]; region[2] = 2; region[3] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(8192), (uint)payload.Length + 1); region[8196] = compression; payload.CopyTo(region, 8197); return region;
    }
    private static async ValueTask WorldHealthChecksRealRegionAllocationCompressionAndBudgets()
    {
        foreach (string scenario in new[] { "raw", "gzip", "zlib", "overlap", "outside", "truncated-file", "chunk-length", "truncated-gzip", "truncated-zlib", "bad-nbt", "unsupported", "external", "missing-external", "region-budget", "running" })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = ResourceInstanceFixture(root), world = Path.Combine(instance, "saves", "Fixture"), regions = Path.Combine(world, "region"); Directory.CreateDirectory(regions);
                await File.WriteAllBytesAsync(Path.Combine(world, "level.dat"), WorldCompletionNbt());
                byte[] nbt = [10, 0, 0, 0]; byte compression = scenario.Contains("gzip", StringComparison.Ordinal) ? (byte)1 : scenario.Contains("zlib", StringComparison.Ordinal) ? (byte)2 : (byte)3;
                byte[] encoded = HealthChunkFixture(compression, nbt);
                if (scenario.StartsWith("truncated-", StringComparison.Ordinal) && scenario != "truncated-file") encoded = encoded[..^3];
                if (scenario == "bad-nbt") encoded = [10, 0, 0];
                byte[] region = HealthRegionFixture(compression, encoded);
                if (scenario == "overlap") { region[6] = 2; region[7] = 1; }
                if (scenario == "outside") region[2] = 3;
                if (scenario == "truncated-file") region = region[..^1];
                if (scenario == "chunk-length") BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(8192), 4093);
                if (scenario == "unsupported") region[8196] = 4;
                if (scenario is "external" or "missing-external") { region[8196] = 131; BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(8192), 1); }
                string path = Path.Combine(regions, "r.0.0.mca"); await File.WriteAllBytesAsync(path, region);
                if (scenario == "external") await File.WriteAllBytesAsync(Path.Combine(regions, "c.0.0.mcc"), nbt);
                if (scenario == "region-budget")
                    for (int index = 1; index <= 256; index++) await File.WriteAllBytesAsync(Path.Combine(regions, "r." + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.mca"), new byte[8192]);
                var store = ContentCompletionStore();
                if (scenario == "running")
                {
                    var session = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 42, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null) { GameDirectory = instance };
                    store.PublishDelta(store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [session], []));
                }
                var result = await InstanceWorldService.ReadHealthAsync(new(instance, "Fixture"), store);
                if (scenario is "raw" or "gzip" or "zlib" or "external") { AssertTrue(result.Healthy); AssertEqual(1, result.Regions); AssertEqual(1, result.Chunks); }
                else { AssertFalse(result.Healthy); AssertFalse(result.Complete); AssertTrue(result.DetectedIssues > 0); }
                AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(region));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
