using System.Buffers.Binary;
using Nexa.Core.Media;
using Nexa.Services.Files;

namespace Nexa.Services.Minecraft.Management;

internal static partial class InstanceContentMetadata
{
    internal const int ScreenshotHeaderLength = 33;
    private const int ScreenshotByteLimit = 16 * 1024 * 1024;

    internal static async Task<InstanceContentEntry> ReadScreenshotAsync(InstanceContentEntry item, string path,
        ArchiveReadBudget previewBudget, ArchiveReadBudget headerBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadScreenshot(item, headerBudget)) return item;
        CheckPath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadScreenshotStreamAsync(item, path, input, previewBudget, headerBudget, token).ConfigureAwait(false);
    }

    // Borrows one already-open stream; preview bytes and header facts share the same file identity.
    internal static async Task<InstanceContentEntry> ReadScreenshotStreamAsync(InstanceContentEntry item, string path,
        Stream input, ArchiveReadBudget previewBudget, ArchiveReadBudget headerBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadScreenshot(item, headerBudget)) return item;
        CheckPath(path);
        FileInfo file = new(path);
        if (!HasScreenshotIdentity(file, item)) return item;
        long expectedSize = item.Size.GetValueOrDefault();
        if (input.Length != expectedSize) return item;
        byte[] header = new byte[ScreenshotHeaderLength];
        int read = await input.ReadAtLeastAsync(header, header.Length, false, token).ConfigureAwait(false);
        headerBudget.Consume(read);
        var dimensions = read == header.Length ? ReadScreenshotHeader(header) : null;
        if (dimensions is null) return item;
        PngImage? image = null;
        if (expectedSize <= previewBudget.Remaining)
        {
            input.Position = 0;
            using MemoryStream output = new();
            await ArchiveReadBudget.CopyAsync(input, output, expectedSize, ScreenshotByteLimit, previewBudget, token).ConfigureAwait(false);
            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            if (!bytes[..ScreenshotHeaderLength].SequenceEqual(header)) return item;
            image = PngImage.TryCreatePreview(bytes);
        }
        token.ThrowIfCancellationRequested();
        CheckPath(path);
        file.Refresh();
        if (!HasScreenshotIdentity(file, item) || input.Length != expectedSize) return item;
        return item with { Icon = image, ImageWidth = dimensions.Value.Width, ImageHeight = dimensions.Value.Height };
    }

    private static bool CanReadScreenshot(InstanceContentEntry item, ArchiveReadBudget headerBudget) => !item.IsDirectory
        && MinecraftVersionPaths.IsSafeReference(item.Name) && item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        && item.Size is >= 57 and <= ScreenshotByteLimit && item.ModifiedUtcTicks > 0
        && headerBudget.Remaining >= ScreenshotHeaderLength;

    private static bool HasScreenshotIdentity(FileInfo file, InstanceContentEntry item) => file.Exists
        && file.Length == item.Size && file.LastWriteTimeUtc.Ticks == item.ModifiedUtcTicks;

    private static (int Width, int Height)? ReadScreenshotHeader(ReadOnlySpan<byte> header)
    {
        // The encoded carrier owns PNG signature/IHDR/dimension bounds. The header-only read
        // additionally admits legal PNG encodings and verifies the complete IHDR checksum.
        if (PngImage.TryCreatePreview(header) is not { } dimensions) return null;
        int depth = header[24], color = header[25];
        bool supported = color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!supported || header[26] != 0 || header[27] != 0 || header[28] > 1) return null;
        uint crc = uint.MaxValue;
        foreach (byte value in header[12..29])
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc == BinaryPrimitives.ReadUInt32BigEndian(header[29..33]) ? (dimensions.Width, dimensions.Height) : null;
    }
}
