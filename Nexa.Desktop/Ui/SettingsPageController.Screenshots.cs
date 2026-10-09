using Nexa.Core.Media;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId ScreenshotPreviewAction = XsrSemanticId.Parse("ui.settings.screenshot-preview.action");
    internal Func<ReadOnlyMemory<byte>, Task>? CopyScreenshotAsync { get; set; }
    internal Func<string, long, long, Task>? ShareScreenshotAsync { get; set; }
    private readonly Dictionary<XsrUiEntityId, Action> _screenshotPreviewActions = [];
    private XsrUiEntityId _screenshotPreview, _screenshotPreviewCard, _screenshotPreviewImage, _screenshotPreviewStatus, _screenshotPreviousFocus;
    private XsrUiEntityId _screenshotCropButton;
    private XsrUiEntityId _cropX, _cropY, _cropWidth, _cropHeight;
    private InstanceScreenshotQuery? _screenshotPreviewQuery;
    private InstanceContentEntry? _screenshotPreviewItem;
    private CancellationTokenSource? _screenshotPreviewStop;
    private Task<XsrResult<InstanceScreenshot>>? _screenshotPreviewRead;
    private Task? _screenshotOperation;
    private long _screenshotPreviewGeneration;
    private Guid _screenshotRemovalDialog;
    private bool _screenshotPreviousFocusVisible, _screenshotCropVisible;
    private XsrUiSize _screenshotPreviewViewport;

    private static bool SameScreenshot(InstanceContentEntry item, InstanceScreenshotQuery file) =>
        item.Name == file.Name && item.Size == file.ExpectedSize && item.ModifiedUtcTicks == file.ExpectedModifiedUtcTicks;

    private bool ScreenshotPreviewCurrent(InstanceScreenshotQuery file, long generation) =>
        Volatile.Read(ref _screenshotPreviewGeneration) == generation && _screenshotPreviewQuery == file
        && _instance == file.InstanceDirectory && _visible && _selected == "screenshots" && _shell.Stage.Navigation.Current == Page;

    private void OpenScreenshotPreview(InstanceContentEntry item, XsrUiEntityId source)
    {
        if (_instance is null || item.IsDirectory) return;
        CloseScreenshotPreview(restoreFocus: false);
        _screenshotPreviousFocus = _shell.Renderer.Focused.IsAssigned ? _shell.Renderer.Focused : source;
        _screenshotPreviousFocusVisible = _shell.Tree.GetComponent<XsrUiInput>(_screenshotPreviousFocus)?.IsFocusVisible == true;
        _screenshotPreviewItem = item;
        _screenshotPreviewQuery = item.Size is { } size ? new(_instance, item.Name, size, item.ModifiedUtcTicks) : null;
        _screenshotPreviewStop = new();
        _screenshotPreview = Element(_shell.Stage.Root, "ScreenshotPreviewLayer", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(_screenshotPreview)!.VerticalAlignment = XsrUiAlignment.Stretch;
        Style(_screenshotPreview, new(15, 22, 34, 180), Ink, 0);
        _shell.Tree.SetComponent(_screenshotPreview, new XsrUiDismissBinding(ScreenshotPreviewAction));
        _screenshotPreviewActions[_screenshotPreview] = () => CloseScreenshotPreview();
        _shell.Tree.SetComponent(_screenshotPreview, new XsrUiOverlayMotion(XsrUiOverlayMotionKind.DialogScrim));
        BuildScreenshotPreviewCard();
        _shell.Stage.Show(_screenshotPreview, modal: true);
        FocusScreenshotPreviewClose();
        if (_screenshotPreviewQuery is { } query && _queries.TryResolve(InstanceScreenshotContract.Read, out var route))
        {
            _screenshotPreviewRead = _queries.QueryAsync<InstanceScreenshotQuery, InstanceScreenshot>(route, query,
                cancellationToken: _screenshotPreviewStop.Token).AsTask();
            ObserveTransfer(_screenshotPreviewRead);
        }
    }

    private void BuildScreenshotPreviewCard()
    {
        if (_screenshotPreviewItem is not { } item || !_shell.Tree.IsAlive(_screenshotPreview)) return;
        string? focusedName = _shell.Tree.IsAlive(_shell.Renderer.Focused) ? _shell.Tree.Name(_shell.Renderer.Focused) : null;
        var drafts = new Dictionary<string, string>();
        _shell.Tree.Walk(_screenshotPreview, entity =>
        {
            if (_shell.Tree.GetComponent<XsrUiTextInput>(entity) is { } input) drafts[_shell.Tree.Name(entity)] = input.ReadDraft();
            return true;
        });
        foreach (var action in _screenshotPreviewActions.Keys.Where(entity => entity != _screenshotPreview).ToArray()) _screenshotPreviewActions.Remove(action);
        foreach (var child in _shell.Tree.Children(_screenshotPreview).ToArray()) _shell.Tree.Destroy(child);
        _screenshotPreviewViewport = _shell.Renderer.Viewport;
        _screenshotPreviewCard = Stack(_screenshotPreview, "ScreenshotPreview", XsrUiOrientation.Vertical, 0);
        var layout = _shell.Tree.GetComponent<XsrUiElement>(_screenshotPreviewCard)!;
        layout.Width = Math.Max(240, Math.Min(1120, _screenshotPreviewViewport.Width - 80));
        layout.MaxHeight = Math.Max(200, _screenshotPreviewViewport.Height - 56);
        layout.HorizontalAlignment = layout.VerticalAlignment = XsrUiAlignment.Center;
        _shell.Tree.SetComponent(_screenshotPreviewCard, new XsrUiSemantic(XsrUiSemanticRole.Dialog, "截图预览"));
        _shell.Tree.SetComponent(_screenshotPreviewCard, new XsrUiScroll { ShowsVerticalIndicator = true });
        _shell.Tree.SetComponent(_screenshotPreviewCard, new XsrUiScrollGesture());
        _shell.Tree.SetComponent(_screenshotPreviewCard, new XsrUiOverlayMotion(XsrUiOverlayMotionKind.Dialog));
        Style(_screenshotPreviewCard, White, Ink, 18);
        // UI.Next paints the element's content box. Keep the rounded surface separate
        // from its padded contents so the 16-DIP inset remains visible inside the card.
        var body = Stack(_screenshotPreviewCard, "ScreenshotPreviewBody", XsrUiOrientation.Vertical, 10);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = new(16, 16, 16, 16);
        var header = Stack(body, "ScreenshotPreviewHeader", XsrUiOrientation.Horizontal, 12);
        var title = Element(header, "ScreenshotPreviewTitle", XsrUiSemanticRole.Text, item.Name, height: 34);
        _shell.Tree.SetComponent(title, new XsrUiText(item.Name) { MaxLines = 1, TrimOverflow = true });
        Style(title, XsrUiColor.Transparent, Ink, 0, 17, 600);
        _shell.Tree.GetComponent<XsrUiElement>(title)!.Weight = 1;
        DesktopLiteralText.Preserve(_shell.Tree, title);
        ScreenshotPreviewButton(header, "Close", "关闭", () => CloseScreenshotPreview(), 72);
        _screenshotPreviewImage = ContentImage(body, item, null,
            Math.Max(120, Math.Min(_screenshotCropVisible ? 360 : 680, _screenshotPreviewViewport.Height - (_screenshotCropVisible ? 520 : 248))), "ScreenshotPreviewImage");
        _shell.Tree.SetComponent(_screenshotPreviewImage, new XsrUiSemantic(XsrUiSemanticRole.Image, item.Name) { Localize = false });
        Text(body, "图像尺寸", 11, Muted, 20);
        string? dimensions = item.Icon is { } image ? $"{image.Width} × {image.Height}"
            : item is { ImageWidth: > 0, ImageHeight: > 0 } ? $"{item.ImageWidth} × {item.ImageHeight}" : null;
        _screenshotPreviewStatus = Text(body, dimensions is null ? "暂时无法预览此图片，可在文件夹中查看原文件。"
            : dimensions + " · " + FormatContentSize(item.Size ?? 0), 12, Muted, 24);
        if (dimensions is not null) DesktopLiteralText.Preserve(_shell.Tree, _screenshotPreviewStatus);
        var actions = Stack(body, "ScreenshotPreviewActions", XsrUiOrientation.Vertical, 8);
        var actionRow = Stack(actions, "ScreenshotPreviewActionRow", XsrUiOrientation.Horizontal, 8);
        double actionWidth = 0;
        void Button(string name, string label, Action action, double width, bool enabled = true)
        {
            if (actionWidth > 0 && actionWidth + 8 + width > layout.Width - 44)
            { actionRow = Stack(actions, "ScreenshotPreviewActionRow", XsrUiOrientation.Horizontal, 8); actionWidth = 0; }
            ScreenshotPreviewButton(actionRow, name, label, action, width, enabled);
            actionWidth += (actionWidth > 0 ? 8 : 0) + width;
        }
        _screenshotCropButton = default;
        bool readable = _screenshotPreviewQuery is not null && item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && _queries.TryResolve(InstanceScreenshotContract.Read, out _);
        if (CopyScreenshotAsync is not null) Button("Copy", "复制图片", () => StartScreenshotOperation(copy: true), 96, readable);
        if (ShareScreenshotAsync is not null) Button("Share", "分享截图", () => StartScreenshotOperation(copy: false), 96, readable);
        string? directory = _management?.Pages.FirstOrDefault(page => page.Id == "screenshots")?.Directory;
        if (directory is not null && OpenManagementDirectory is not null) Button("Folder", "打开所在文件夹", () => OpenContentDirectory(directory), 128);
        if (_commands.TryResolve(InstanceManagementContract.RemoveContent, out _)) Button("Remove", "移至已移除内容", () => RemoveContent(_screenshotPreviewItem!), 128);
        if (_commands.TryResolve(InstanceScreenshotContract.Crop, out _)) Button("Crop", _screenshotCropVisible ? "收起裁剪" : "裁剪副本", () =>
        { _screenshotCropVisible = !_screenshotCropVisible; BuildScreenshotPreviewCard(); FocusScreenshotPreviewClose(); }, 96, readable && item.Icon is not null);
        if (_screenshotCropVisible) BuildScreenshotCrop(body, item);
        XsrUiEntityId restoredFocus = default;
        _shell.Tree.Walk(_screenshotPreviewCard, entity =>
        {
            if (_shell.Tree.GetComponent<XsrUiTextInput>(entity) is not null && drafts.TryGetValue(_shell.Tree.Name(entity), out var draft))
                _shell.Renderer.SetTextInputValue(entity, draft);
            if (_shell.Tree.Name(entity) == focusedName) restoredFocus = entity;
            return true;
        });
        if (restoredFocus.IsAssigned) _shell.Renderer.Focus(restoredFocus, _screenshotPreviousFocusVisible);
    }

    private void ScreenshotPreviewButton(XsrUiEntityId parent, string name, string label, Action action, double width, bool enabled = true)
    {
        var entity = ActionButton(parent, "ScreenshotPreview." + name, label, ScreenshotPreviewAction, width);
        _shell.Tree.GetComponent<XsrUiInput>(entity)!.Enabled = enabled;
        _screenshotPreviewActions[entity] = action;
        if (name == "Crop") _screenshotCropButton = entity;
    }

    private void FocusScreenshotPreviewClose()
    {
        var close = _screenshotPreviewActions.Keys.FirstOrDefault(entity => _shell.Tree.IsAlive(entity) && _shell.Tree.Name(entity) == "ScreenshotPreview.Close");
        if (close.IsAssigned) _shell.Renderer.Focus(close, _screenshotPreviousFocusVisible);
    }

    private void HandleScreenshotPreviewIntent(DesktopUiIntent intent)
    {
        if (_screenshotPreview.IsAssigned && _shell.Tree.IsAlive(intent.Source) && _visible && _selected == "screenshots"
            && _shell.Stage.Navigation.Current == Page && _screenshotPreviewActions.TryGetValue(intent.Source, out var action)
            && _shell.Tree.GetComponent<XsrUiInput>(intent.Source)?.Enabled != false) action();
    }

    private void UpdateScreenshotPreview()
    {
        if (!_screenshotPreview.IsAssigned) return;
        if (_selected != "screenshots" || !_visible || _shell.Stage.Navigation.Current != Page
            || _screenshotPreviewQuery is { } current && (current.InstanceDirectory != _instance
                || _management?.Contents.FirstOrDefault(page => page.PageId == "screenshots")?.Entries.Any(item => SameScreenshot(item, current)) != true))
        { CloseScreenshotPreview(restoreFocus: false); return; }
        if (_screenshotPreviewRead is { IsCompleted: true } reading)
        {
            _screenshotPreviewRead = null;
            if (PendingQuery.Succeeded(reading) && _screenshotPreviewItem is { } item)
            {
                _screenshotPreviewItem = item with { Icon = reading.Result.Value!.Image };
                var image = reading.Result.Value.Image;
                _shell.Tree.GetComponent<XsrUiImage>(_screenshotPreviewImage)!.Raster = new(image,
                    [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))])
                { FitToBounds = true };
                SetScreenshotPreviewStatus($"{image.Width} × {image.Height} · {FormatContentSize(item.Size ?? 0)}", literal: true);
                if (_screenshotCropButton.IsAssigned && _shell.Tree.IsAlive(_screenshotCropButton))
                {
                    _shell.Tree.GetComponent<XsrUiInput>(_screenshotCropButton)!.Enabled = true;
                    _shell.Tree.MarkDirty(_screenshotCropButton, XsrUiDirtyKinds.Paint);
                }
                _shell.Tree.MarkDirty(_screenshotPreviewImage, XsrUiDirtyKinds.Paint);
                _shell.Tree.MarkDirty(_screenshotPreviewStatus, XsrUiDirtyKinds.Paint);
            }
            else if (_shell.Tree.IsAlive(_screenshotPreviewStatus))
            {
                SetScreenshotPreviewStatus("无法读取原图，请刷新后重试；仍可打开所在文件夹。", literal: false);
            }
        }
        if (_screenshotOperation is { IsCompleted: true } operation)
        {
            _screenshotOperation = null;
            if (operation.IsCompletedSuccessfully) _feedback.Info("截图操作已完成。");
            else if (!operation.IsCanceled) { _ = operation.Exception; _feedback.Error("截图操作未完成，请刷新后重试。"); }
        }
        if (_screenshotPreviewViewport != _shell.Renderer.Viewport) BuildScreenshotPreviewCard();
    }

    private void SetScreenshotPreviewStatus(string content, bool literal)
    {
        var text = _shell.Tree.GetComponent<XsrUiText>(_screenshotPreviewStatus)!;
        text.Content = content; text.Localize = !literal;
        if (_shell.Tree.GetComponent<XsrUiSemantic>(_screenshotPreviewStatus) is { } semantic)
        { semantic.Label = content; semantic.Localize = !literal; }
        _shell.Tree.MarkDirty(_screenshotPreviewStatus, XsrUiDirtyKinds.Paint);
    }

    private void StartScreenshotOperation(bool copy)
    {
        if (_screenshotOperation is not null || _managementWrite is not null || _screenshotPreviewQuery is not { } file
            || _screenshotPreviewStop is not { } stop || !_queries.TryResolve(InstanceScreenshotContract.Read, out var route)) return;
        long generation = _screenshotPreviewGeneration;
        var token = stop.Token;
        async Task Apply()
        {
            var result = await _queries.QueryAsync<InstanceScreenshotQuery, InstanceScreenshot>(route, file, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ScreenshotPreviewCurrent(file, generation)) return;
            if (!result.IsSuccess) throw new IOException(result.Error?.Message);
            if (copy && CopyScreenshotAsync is { } copyAction) await copyAction(result.Value!.Image.Bytes).ConfigureAwait(false);
            else if (!copy && ShareScreenshotAsync is { } shareAction) await shareAction(result.Value!.Path, file.ExpectedSize, file.ExpectedModifiedUtcTicks).ConfigureAwait(false);
        }
        _screenshotOperation = Apply(); ObserveTransfer(_screenshotOperation);
    }

    private void BuildScreenshotCrop(XsrUiEntityId parent, InstanceContentEntry item)
    {
        if (item.Icon is not { } image || _screenshotPreviewQuery is not { } query) return;
        Text(parent, "裁剪为新截图", 14, Ink, 28, 600);
        var fields = Stack(parent, "ScreenshotCropFields", XsrUiOrientation.Horizontal, 16);
        var left = Stack(fields, "ScreenshotCropPosition", XsrUiOrientation.Vertical, 6);
        var right = Stack(fields, "ScreenshotCropDimensions", XsrUiOrientation.Vertical, 6);
        _shell.Tree.GetComponent<XsrUiElement>(left)!.Weight = _shell.Tree.GetComponent<XsrUiElement>(right)!.Weight = 1;
        _cropX = ManagementField(left, "ScreenshotCropX", "左侧像素", "0");
        _cropY = ManagementField(left, "ScreenshotCropY", "顶部像素", "0");
        _cropWidth = ManagementField(right, "ScreenshotCropWidth", "宽度", image.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _cropHeight = ManagementField(right, "ScreenshotCropHeight", "高度", image.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ScreenshotPreviewButton(parent, "SaveCrop", "保存裁剪副本", () =>
        {
            if (_screenshotPreviewQuery != query || _instance != query.InstanceDirectory) return;
            int? Read(XsrUiEntityId entity) => int.TryParse(_shell.Tree.GetComponent<XsrUiTextInput>(entity)?.ReadDraft(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : null;
            if (Read(_cropX) is not { } x || Read(_cropY) is not { } y || Read(_cropWidth) is not { } width || Read(_cropHeight) is not { } height)
            { _feedback.Error("裁剪区域必须填写整数像素。"); return; }
            DispatchWorld(InstanceScreenshotContract.Crop, new InstanceScreenshotCropCommand(query, x, y, width, height));
        }, 128);
    }

    private void CloseScreenshotPreview(bool restoreFocus = true)
    {
        Interlocked.Increment(ref _screenshotPreviewGeneration);
        _screenshotPreviewStop?.Cancel(); _screenshotPreviewStop?.Dispose(); _screenshotPreviewStop = null;
        _screenshotPreviewRead = null; _screenshotOperation = null; _screenshotPreviewQuery = null; _screenshotPreviewItem = null;
        _screenshotCropVisible = false;
        if (_screenshotRemovalDialog != Guid.Empty) _feedback.DismissDialog(_screenshotRemovalDialog);
        _screenshotRemovalDialog = default;
        if (_screenshotPreview.IsAssigned && _shell.Tree.IsAlive(_screenshotPreview))
        { _shell.Stage.Dismiss(_screenshotPreview); _shell.Tree.Destroy(_screenshotPreview); }
        bool hadPreview = _screenshotPreview.IsAssigned;
        _screenshotPreview = default; _screenshotPreviewActions.Clear();
        if (restoreFocus && _screenshotPreviousFocus.IsAssigned && _shell.Tree.IsAlive(_screenshotPreviousFocus))
            _shell.Renderer.Focus(_screenshotPreviousFocus, _screenshotPreviousFocusVisible);
        else if (hadPreview && _shell.Renderer.Focused.IsAssigned && !_shell.Tree.IsAlive(_shell.Renderer.Focused)) _shell.Renderer.Focus(default);
        _screenshotPreviousFocus = default;
    }
}
