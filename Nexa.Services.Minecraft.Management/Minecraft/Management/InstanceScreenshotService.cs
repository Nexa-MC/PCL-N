using Nexa.Core.Media;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceScreenshotService
{
    public static async Task<InstanceScreenshot> ReadAsync(InstanceScreenshotQuery query, CancellationToken token = default)
    {
        if (!MinecraftVersionPaths.IsSafeReference(query.Name) || !query.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 PNG 截图。");
        var snapshot = await InstanceManagementService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        string path = Path.Combine(snapshot.GameDirectory, "screenshots", query.Name); RecoveryBlobStore.CheckLinks(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != query.ExpectedSize || file.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks || file.Length > 16 * 1024 * 1024)
            throw new IOException("截图已变化或超过读取预算，请刷新后重试。");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        byte[] bytes = new byte[(int)file.Length + 1]; int count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
        file.Refresh();
        if (count != query.ExpectedSize || file.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks) throw new IOException("截图读取期间变化。");
        var image = PngImage.TryCreatePreview(bytes.AsSpan(0, count)) ?? throw new InvalidDataException("截图不是受支持的 PNG。");
        return new(path, image);
    }

    public static async Task<XsrResult> CropAsync(InstanceScreenshotCropCommand command, XsrStateStore store,
        Func<ReadOnlyMemory<byte>, int, int, int, int, byte[]> encodeCrop, CancellationToken token = default)
    {
        string? stage = null;
        try
        {
            var snapshot = await InstanceManagementService.ReadAsync(new(command.File.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterOperationAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            var screenshot = await ReadAsync(command.File, token).ConfigureAwait(false);
            if (command.X < 0 || command.Y < 0 || command.Width <= 0 || command.Height <= 0
                || (long)command.X + command.Width > screenshot.Image.Width || (long)command.Y + command.Height > screenshot.Image.Height)
                throw new InvalidDataException("裁剪区域超出截图尺寸。");
            byte[] bytes = encodeCrop(screenshot.Image.Bytes, command.X, command.Y, command.Width, command.Height);
            var image = PngImage.TryCreatePreview(bytes);
            if (image is null || image.Width != command.Width || image.Height != command.Height) throw new InvalidDataException("裁剪结果不是有效 PNG。");
            string directory = Path.GetDirectoryName(screenshot.Path)!;
            stage = Path.Combine(directory, ".nexa-screenshot-" + Guid.NewGuid().ToString("N")); RecoveryBlobStore.CheckLinks(stage);
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            string stem = Path.GetFileNameWithoutExtension(command.File.Name);
            if (stem.Length > 150) stem = stem[..150];
            string target = Path.Combine(directory, stem + "-crop-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target); File.Move(stage, target, false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally { if (stage is not null && File.Exists(stage)) File.Delete(stage); }
    }
}
