using Nexa.Core.Media;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceScreenshotQuery(string InstanceDirectory, string Name, long ExpectedSize, long ExpectedModifiedUtcTicks);
public sealed record InstanceScreenshot(string Path, PngImage Image);
public sealed record InstanceScreenshotCropCommand(InstanceScreenshotQuery File, int X, int Y, int Width, int Height);
public static class InstanceScreenshotContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.screenshot.read");
    public static readonly XsrSemanticId Crop = XsrSemanticId.Parse("minecraft.instance.screenshot.crop");
}
