using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    internal Func<ReadOnlyMemory<byte>, Task>? CopyScreenshotAsync { get; set; }
    internal Func<string, Task>? ShareScreenshotAsync { get; set; }
    private XsrUiEntityId _cropX, _cropY, _cropWidth, _cropHeight;

    private void BuildScreenshotActions(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (_instance is null || item.Size is not { } size || item.IsDirectory || !item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        var query = new InstanceScreenshotQuery(_instance, item.Name, size, item.ModifiedUtcTicks);
        if (item.Icon is { } preview)
        {
            byte depth = preview.Bytes.Span[24], color = preview.Bytes.Span[25];
            if (depth is 1 or 2 or 4 or 8 or 16) ManagementFactIn(parent, "位深", depth + " bit");
            ManagementFactIn(parent, "颜色类型", color switch { 0 => "灰度", 2 => "RGB", 3 => "索引颜色", 4 => "灰度与透明度", 6 => "RGBA", _ => "未知" });
        }
        var actions = Stack(parent, "ScreenshotActions", XsrUiOrientation.Horizontal, 10);
        if (CopyScreenshotAsync is not null) ManagementButton(actions, "复制图片", () => StartWorldPlatformAction(async () =>
        {
            if (!_queries.TryResolve(InstanceScreenshotContract.Read, out var route)) return;
            var result = await _queries.QueryAsync<InstanceScreenshotQuery, InstanceScreenshot>(route, query).ConfigureAwait(false);
            if (!result.IsSuccess) throw new IOException(result.Error?.Message);
            await CopyScreenshotAsync!(result.Value!.Image.Bytes).ConfigureAwait(false);
        }, "图片已复制到剪贴板。"), 96);
        if (ShareScreenshotAsync is not null) ManagementButton(actions, "分享截图", () => StartWorldPlatformAction(async () =>
        {
            if (!_queries.TryResolve(InstanceScreenshotContract.Read, out var route)) return;
            var result = await _queries.QueryAsync<InstanceScreenshotQuery, InstanceScreenshot>(route, query).ConfigureAwait(false);
            if (!result.IsSuccess) throw new IOException(result.Error?.Message);
            await ShareScreenshotAsync!(result.Value!.Path).ConfigureAwait(false);
        }, "截图文件已复制，可粘贴到聊天应用分享。"), 96);
        if (item.Icon is not { } image || !_commands.TryResolve(InstanceScreenshotContract.Crop, out _)) return;
        Text(parent, "裁剪为新截图", 16, Ink, 28, 600);
        _cropX = ManagementField(parent, "ScreenshotCropX", "左侧像素", "0");
        _cropY = ManagementField(parent, "ScreenshotCropY", "顶部像素", "0");
        _cropWidth = ManagementField(parent, "ScreenshotCropWidth", "宽度", image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _cropHeight = ManagementField(parent, "ScreenshotCropHeight", "高度", image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ManagementButton(parent, "保存裁剪副本", () =>
        {
            int? Read(XsrUiEntityId entity) => int.TryParse(_shell.Tree.GetComponent<XsrUiTextInput>(entity)?.ReadDraft(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : null;
            if (Read(_cropX) is not { } x || Read(_cropY) is not { } y || Read(_cropWidth) is not { } width || Read(_cropHeight) is not { } height)
            { _feedback.Error("裁剪区域必须填写整数像素。"); return; }
            DispatchWorld(InstanceScreenshotContract.Crop, new InstanceScreenshotCropCommand(query, x, y, width, height));
        }, 128);
    }
}
