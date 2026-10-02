using System.Formats.Tar;
using System.IO.Compression;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask UpdateModesCannotPropagateSpecialOrSharedWritePermissions()
    {
        string root = CreateTempDirectory();
        try
        {
            int[] modes = [0xFFF, 0xE00, 0x1FF, 384, 420, 493];
            string zipPath = Path.Combine(root, "modes.zip"), tarPath = Path.Combine(root, "modes.tar");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (int mode in modes)
                    foreach (int type in new[] { 0, 0x8000 })
                    {
                        var entry = archive.CreateEntry($"{mode}-{type}");
                        entry.ExternalAttributes = (type | mode) << 16;
                        using var output = entry.Open();
                        output.WriteByte(42);
                    }
            using (var writer = new TarWriter(File.Create(tarPath)))
                foreach (int mode in modes)
                {
                    using var content = new MemoryStream([42]);
                    writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, mode.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    { Mode = (UnixFileMode)mode, DataStream = content });
                }
            string zipRoot = Path.Combine(root, "zip"), tarRoot = Path.Combine(root, "tar");
            var zip = await UpdatePayloadExtractor.ExtractZipAsync(zipPath, zipRoot);
            var tar = await UpdatePayloadExtractor.ExtractTarAsync(tarPath, tarRoot);
            foreach (int mode in modes)
            {
                int expected = mode & 0x1ED;
                foreach (int type in new[] { 0, 0x8000 })
                {
                    string name = $"{mode}-{type}";
                    AssertEqual(expected, zip.Single(file => file.Path == name).UnixMode);
                    if (!OperatingSystem.IsWindows()) AssertEqual((UnixFileMode)expected, File.GetUnixFileMode(Path.Combine(zipRoot, name)));
                }
                string tarName = mode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AssertEqual(expected, tar.Single(file => file.Path == tarName).UnixMode);
                if (!OperatingSystem.IsWindows()) AssertEqual((UnixFileMode)expected, File.GetUnixFileMode(Path.Combine(tarRoot, tarName)));
            }

            // Scatter manifests reach a different chmod sink after digest verification.
            string staged = Path.Combine(root, "scatter");
            Directory.CreateDirectory(staged);
            await File.WriteAllBytesAsync(Path.Combine(staged, "app"), [42]);
            var manifest = new UpdateScatterPatchManifest
            {
                TargetFiles = [new() { Path = "app", Sha256 = OrchSha([42]), Size = 1, UnixMode = 0xFFF }],
            };
            await new UpdatePatchApplier(new FakeRunner()).ApplyScatterOpsAsync(manifest, zipPath, root, staged);
            if (!OperatingSystem.IsWindows()) AssertEqual((UnixFileMode)493, File.GetUnixFileMode(Path.Combine(staged, "app")));

            // This entry point is exercised by the existing Windows/macOS/Linux account-security
            // matrix, so the helper persistence primitive receives native filesystem coverage too.
            await UpdateHighWaterStoreIsMonotonicAndDurable();
        }
        finally { Directory.Delete(root, true); }
    }
}
