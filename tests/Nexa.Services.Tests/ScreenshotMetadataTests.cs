using System.Buffers.Binary;
using Nexa.Core.Media;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ScreenshotDimensionsHaveAnIndependentBoundedHeaderBudget()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "landscape.png");
            await File.WriteAllBytesAsync(path, WardrobeCreatePng(320, 180));
            var entry = ScreenshotMetadataEntry(path);
            ArchiveReadBudget previews = new(64 * 1024 * 1024);
            previews.Consume(previews.Remaining);
            var gallery = await InstanceContentMetadata.EnrichAsync(new("screenshots", [entry], true, null), root, previews, default);
            AssertEqual<int?>(320, gallery.Entries[0].ImageWidth);
            AssertEqual<int?>(180, gallery.Entries[0].ImageHeight);
            AssertTrue(gallery.Entries[0].Icon is null);
            AssertEqual(0L, previews.Remaining);
            var complete = await InstanceContentMetadata.EnrichAsync(new("screenshots", [entry], true, null), root, new(entry.Size!.Value), default);
            AssertTrue(complete.Entries[0].Icon is not null);
            AssertEqual<int?>(complete.Entries[0].Icon!.Width, complete.Entries[0].ImageWidth);
            AssertEqual<int?>(complete.Entries[0].Icon!.Height, complete.Entries[0].ImageHeight);

            // A header read has exactly one small allowance; it never probes or charges image bytes.
            using (var grow = new FileStream(path, FileMode.Open, FileAccess.Write)) grow.SetLength(8 * 1024 * 1024);
            entry = ScreenshotMetadataEntry(path);
            ArchiveReadBudget headers = new(33), untouchedPreviews = new(1024);
            using ScreenshotMetadataReadStream input = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1));
            var header = await InstanceContentMetadata.ReadScreenshotStreamAsync(entry, path, input, untouchedPreviews, headers, default);
            AssertEqual<int?>(320, header.ImageWidth);
            AssertEqual<int?>(180, header.ImageHeight);
            AssertTrue(header.Icon is null);
            AssertEqual(0L, headers.Remaining);
            AssertEqual(1024L, untouchedPreviews.Remaining);
            AssertEqual(1, input.ReadCalls); AssertEqual(33, input.ReadBytes);
            var denied = await InstanceContentMetadata.ReadScreenshotAsync(entry, path, untouchedPreviews, new(32), default);
            AssertTrue(denied.ImageWidth is null && denied.ImageHeight is null);

            // The production header allowance covers the existing 10,000-entry scan limit.
            // Even an internal caller exceeding that admission cannot grow the metadata byte budget.
            var overLimit = await InstanceContentMetadata.EnrichAsync(new("screenshots",
                Enumerable.Repeat(entry, 10001).ToArray(), false, null), root, new(0), default);
            AssertEqual(10000, overLimit.Entries.Count(item => item.ImageWidth == 320 && item.ImageHeight == 180));
            AssertTrue(overLimit.Entries[^1].ImageWidth is null && overLimit.Entries[^1].ImageHeight is null);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ScreenshotHeadersRejectMalformedAndUnsupportedFacts()
    {
        string root = CreateTempDirectory();
        try
        {
            byte[] png = WardrobeCreatePng(20, 10);
            string path = Path.Combine(root, "screen.png");
            async Task<InstanceContentEntry> Read(byte[] bytes)
            {
                await File.WriteAllBytesAsync(path, bytes);
                var entry = ScreenshotMetadataEntry(path) with { Icon = PngImage.TryCreatePreview(png), ImageWidth = 777, ImageHeight = 888 };
                var result = await InstanceContentMetadata.EnrichAsync(new("screenshots", [entry], true, null), root, new(0), default);
                return result.Entries[0];
            }
            byte[] Header(Action<byte[]> mutate) => WardrobeRewriteChunk(png, "IHDR", bytes => { mutate(bytes); return bytes; });
            List<byte[]> invalid =
            [
                Header(bytes => BinaryPrimitives.WriteInt32BigEndian(bytes, 0)),
                Header(bytes => BinaryPrimitives.WriteInt32BigEndian(bytes, 4097)),
                Header(bytes => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), -1)),
                Header(bytes => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 4097)),
                Header(bytes => bytes[8] = 4), // RGBA has no four-bit encoding.
                Header(bytes => bytes[9] = 1),
                Header(bytes => bytes[10] = 1),
                Header(bytes => bytes[11] = 1),
                Header(bytes => bytes[12] = 2),
                png[..8], png[..32], png[..33], png[..56]
            ];
            foreach (int index in new[] { 0, 8, 12, 29 })
            {
                byte[] corrupt = (byte[])png.Clone(); corrupt[index] ^= 1; invalid.Add(corrupt);
            }
            foreach (byte[] bytes in invalid)
            {
                var result = await Read(bytes);
                AssertTrue(result.Icon is null && result.ImageWidth is null && result.ImageHeight is null);
            }
            foreach (var format in new (byte Depth, byte Color)[]
                { (1, 0), (2, 0), (4, 0), (8, 0), (16, 0), (8, 2), (16, 2), (1, 3), (2, 3), (4, 3), (8, 3), (8, 4), (16, 4), (8, 6), (16, 6) })
            {
                var result = await Read(Header(bytes => { bytes[8] = format.Depth; bytes[9] = format.Color; bytes[12] = 1; }));
                AssertEqual<int?>(20, result.ImageWidth); AssertEqual<int?>(10, result.ImageHeight);
            }
            var largest = await Read(Header(bytes =>
            {
                BinaryPrimitives.WriteInt32BigEndian(bytes, 4096);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 4096);
            }));
            AssertEqual<int?>(4096, largest.ImageWidth); AssertEqual<int?>(4096, largest.ImageHeight);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ScreenshotMetadataRetiresChangedIdentityAndRejectsLinks()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "screen.png");
            byte[] png = WardrobeCreatePng(20, 10);
            await File.WriteAllBytesAsync(path, png);
            var entry = ScreenshotMetadataEntry(path);
            async Task Unknown(InstanceContentEntry candidate)
            {
                var source = candidate with { Icon = PngImage.TryCreatePreview(png), ImageWidth = 20, ImageHeight = 10 };
                var result = await InstanceContentMetadata.EnrichAsync(new("screenshots", [source], true, null), root, new(1024), default);
                AssertTrue(result.Entries[0].Icon is null && result.Entries[0].ImageWidth is null && result.Entries[0].ImageHeight is null);
            }
            File.SetLastWriteTimeUtc(path, new DateTime(entry.ModifiedUtcTicks, DateTimeKind.Utc).AddSeconds(2));
            await Unknown(entry);
            await Unknown(ScreenshotMetadataEntry(path) with { Size = png.Length + 1 });
            await Unknown(ScreenshotMetadataEntry(path) with { ModifiedUtcTicks = 0 });
            await Unknown(ScreenshotMetadataEntry(path) with { IsDirectory = true });
            await Unknown(ScreenshotMetadataEntry(path) with { Name = "../screen.png" });
            await File.WriteAllBytesAsync(path, png);
            entry = ScreenshotMetadataEntry(path);
            using (ScreenshotMetadataReadStream changing = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1),
                () => File.SetLastWriteTimeUtc(path, new DateTime(entry.ModifiedUtcTicks, DateTimeKind.Utc).AddSeconds(2))))
            {
                var result = await InstanceContentMetadata.ReadScreenshotStreamAsync(entry, path, changing, new(1024), new(33), default);
                AssertTrue(changing.ReadBytes > 33);
                AssertTrue(result.Icon is null && result.ImageWidth is null && result.ImageHeight is null);
            }
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(root, "linked.png"); File.CreateSymbolicLink(link, path);
                await Unknown(ScreenshotMetadataEntry(link));
            }
            using var stop = new CancellationTokenSource(); stop.Cancel();
            ArchiveReadBudget previews = new(1024), headers = new(33);
            bool cancelled = false;
            try { await InstanceContentMetadata.ReadScreenshotAsync(ScreenshotMetadataEntry(path), path, previews, headers, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled); AssertEqual(1024L, previews.Remaining); AssertEqual(33L, headers.Remaining);
            using var midReadStop = new CancellationTokenSource();
            using ScreenshotMetadataReadStream cancelling = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1), midReadStop.Cancel);
            cancelled = false;
            try { await InstanceContentMetadata.ReadScreenshotStreamAsync(ScreenshotMetadataEntry(path), path, cancelling, new(0), new(33), midReadStop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled); AssertEqual(1, cancelling.ReadCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static InstanceContentEntry ScreenshotMetadataEntry(string path)
    {
        FileInfo file = new(path);
        return new(file.Name, false, file.Length) { ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks };
    }

    private sealed class ScreenshotMetadataReadStream(Stream input, Action? afterFirstRead = null) : Stream
    {
        internal int ReadCalls { get; private set; }
        internal int ReadBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            ReadCalls++; ReadBytes += read;
            if (ReadCalls == 1) afterFirstRead?.Invoke();
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
