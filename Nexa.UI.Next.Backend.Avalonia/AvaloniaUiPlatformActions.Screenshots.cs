using System.Buffers.Binary;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    private Bitmap? _clipboardScreenshot;

    public Task CopyScreenshotAsync(string path) => Dispatcher.UIThread.CheckAccess()
        ? CopyScreenshotOnUiAsync(path) : Dispatcher.UIThread.InvokeAsync(() => CopyScreenshotOnUiAsync(path));

    public Task CopyScreenshotAsync(ReadOnlyMemory<byte> encoded) => Dispatcher.UIThread.CheckAccess()
        ? CopyScreenshotBytesOnUiAsync(encoded) : Dispatcher.UIThread.InvokeAsync(() => CopyScreenshotBytesOnUiAsync(encoded));

    private async Task CopyScreenshotOnUiAsync(string path)
    {
        await CopyScreenshotBytesOnUiAsync(ReadScreenshot(path)).ConfigureAwait(true);
    }

    private async Task CopyScreenshotBytesOnUiAsync(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length is < 33 or > 16 * 1024 * 1024) throw new InvalidDataException("截图大小超出复制预算。");
        var header = encoded.Span;
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("截图不是有效 PNG。");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width is 0 or > 8192 || height is 0 or > 8192 || (ulong)width * height > 32 * 1024 * 1024)
            throw new InvalidDataException("截图尺寸超出复制预算。");
        using var stream = new MemoryStream(encoded.ToArray(), writable: false);
        Bitmap bitmap = new(stream);
        try
        {
            IClipboard clipboard = _owner?.Clipboard ?? throw new InvalidOperationException("The native clipboard is not ready.");
            await clipboard.SetBitmapAsync(bitmap).ConfigureAwait(true);
            Bitmap? previous = _clipboardScreenshot;
            _clipboardScreenshot = bitmap;
            previous?.Dispose();
            try { await clipboard.FlushAsync().ConfigureAwait(true); } catch (NotSupportedException) { }
        }
        catch { if (!ReferenceEquals(_clipboardScreenshot, bitmap)) bitmap.Dispose(); throw; }
    }

    public Task ShareScreenshotAsync(string path) => Dispatcher.UIThread.CheckAccess()
        ? ShareScreenshotOnUiAsync(path) : Dispatcher.UIThread.InvokeAsync(() => ShareScreenshotOnUiAsync(path));

    private async Task ShareScreenshotOnUiAsync(string path)
    {
        _ = ReadScreenshot(path);
        var owner = _owner ?? throw new InvalidOperationException("The native window is not ready.");
        var file = await owner.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path))).ConfigureAwait(true)
            ?? throw new IOException("The screenshot is no longer available.");
        using (file)
        {
            var clipboard = owner.Clipboard ?? throw new InvalidOperationException("The native clipboard is not ready.");
            await clipboard.SetFileAsync(file).ConfigureAwait(true);
            try { await clipboard.FlushAsync().ConfigureAwait(true); } catch (NotSupportedException) { }
        }
    }

    internal static byte[] ReadScreenshot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 PNG 截图。");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 33 or > 16 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("截图文件不存在或超出复制预算。");
        using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[(int)source.Length];
        source.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("截图不是有效 PNG。");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 8192 || height is 0 or > 8192 || (ulong)width * height > 32 * 1024 * 1024)
            throw new InvalidDataException("截图尺寸超出复制预算。");
        return bytes;
    }
}
