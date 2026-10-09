using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private bool _screenshotTimeline;
    private DesktopScreenshotGalleryLayout? _screenshotLayout;
    private IReadOnlyList<InstanceContentEntry>? _screenshotLayoutEntries;
    private IReadOnlyList<IReadOnlyList<DesktopScreenshotGalleryLayout.Slot>>? _screenshotVisibleSlots;
    private double _screenshotLayoutWidth;
    private bool _screenshotLayoutTimeline;

    private static DateTime? ScreenshotTimestamp(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : null;

    private void UpdateScreenshotWaterfall(InstanceContentSnapshot snapshot)
    {
        var viewport = _shell.Renderer.Viewport;
        // These are the retained shell/page constraints, rather than the previous scene's
        // viewport. Resize can prepare this page twice before the next scene is arranged.
        double width = viewport.Width - (_shell.Tree.GetComponent<XsrUiElement>(_shell.Root)?.Padding.Horizontal ?? 0)
            - (_shell.Tree.GetComponent<XsrUiElement>(_shell.Navigation)?.Width ?? XsrUiShell.CollapsedRailWidth)
            - (_shell.Tree.GetComponent<XsrUiElement>(_shell.Content)?.Padding.Horizontal ?? 0) - 100;
        double height = Math.Max(1, viewport.Height);
        width = Math.Max(1, width);
        if (_screenshotLayout is null || !ReferenceEquals(_screenshotLayoutEntries, snapshot.Entries)
            || Math.Abs(width - _screenshotLayoutWidth) > .01 || _screenshotLayoutTimeline != _screenshotTimeline)
        {
            _screenshotLayoutEntries = snapshot.Entries; _screenshotLayoutWidth = width; _screenshotLayoutTimeline = _screenshotTimeline;
            _screenshotLayout = DesktopScreenshotGalleryLayout.Create(snapshot.Entries, width, _screenshotTimeline);
            _screenshotVisibleSlots = null;
        }
        var layout = _screenshotLayout;
        // Toolbar 36 + location 24 + two 12-DIP gaps precede the gallery in this scroll body.
        double offset = Math.Clamp(_shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY - 84,
            0, Math.Max(0, layout.Height - Math.Max(1, height)));
        var visible = layout.SelectWindow(Math.Max(0, offset - 320), offset + Math.Max(1, height) + 320);
        if (_contentWindowStart != -1 && SameScreenshotWindow(_screenshotVisibleSlots, visible)) return;
        _screenshotVisibleSlots = visible; _contentWindowStart = 0;
        foreach (var action in _contentActions) _managementActions.Remove(action);
        _contentActions.Clear(); _screenshotIconEntities.Clear();
        foreach (var child in _shell.Tree.Children(_contentList).ToArray()) _shell.Tree.Destroy(child);
        if (snapshot.Entries.Count == 0) Text(_contentList, _contentFilter.Length == 0 ? "此目录中还没有内容。" : "没有匹配的内容。", 13, Muted, 48);
        var row = Stack(_contentList, "ScreenshotWaterfall", XsrUiOrientation.Horizontal, DesktopScreenshotGalleryLayout.Gap);
        _shell.Tree.GetComponent<XsrUiElement>(row)!.Height = layout.Height;
        for (int index = 0; index < layout.ColumnCount; index++)
        {
            var column = Stack(row, "ScreenshotWaterfallColumn." + index, XsrUiOrientation.Vertical, 0);
            var columnLayout = _shell.Tree.GetComponent<XsrUiElement>(column)!;
            columnLayout.Width = layout.CardWidth; columnLayout.Height = layout.Height; columnLayout.VerticalAlignment = XsrUiAlignment.Start;
            double cursor = 0;
            foreach (var slot in visible[index])
            {
                if (slot.Top > cursor) Element(column, "ScreenshotWaterfallBefore", XsrUiSemanticRole.None, null, height: slot.Top - cursor);
                if (slot.IsHeading)
                {
                    var heading = Text(column, slot.HeadingDate is { } date ? date.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.CurrentCulture) : "修改日期未知", 15, Ink, slot.Height, 600);
                    if (slot.HeadingDate is not null) DesktopLiteralText.Preserve(_shell.Tree, heading);
                }
                else BuildScreenshotCard(column, slot.Item!, slot.ImageHeight, slot.Height, _screenshotTimeline);
                cursor = slot.Top + slot.Height;
            }
            if (layout.Height > cursor) Element(column, "ScreenshotWaterfallAfter", XsrUiSemanticRole.None, null, height: layout.Height - cursor);
        }
        _shell.Tree.MarkDirty(_contentList, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
    }

    private static bool SameScreenshotWindow(IReadOnlyList<IReadOnlyList<DesktopScreenshotGalleryLayout.Slot>>? previous,
        IReadOnlyList<IReadOnlyList<DesktopScreenshotGalleryLayout.Slot>> current)
    {
        if (previous is null || previous.Count != current.Count) return false;
        for (int column = 0; column < current.Count; column++)
            if (!previous[column].SequenceEqual(current[column])) return false;
        return true;
    }

    private void BuildScreenshotCard(XsrUiEntityId parent, InstanceContentEntry item, double imageHeight, double cardHeight, bool includeTimestamp)
    {
        var card = Stack(parent, "ManagementScreenshot." + item.Name, XsrUiOrientation.Vertical, 0);
        _shell.Tree.GetComponent<XsrUiElement>(card)!.Height = cardHeight;
        Style(card, White, Ink, 14);
        _shell.Tree.SetComponent(card, new XsrUiSemantic(XsrUiSemanticRole.Button, "查看截图 " + item.Name));
        _shell.Tree.SetComponent(card, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(card, new XsrUiCommandBinding(ManagementAction));
        RegisterContentAction(card, () => OpenScreenshotPreview(item, card));
        var body = Stack(card, "ManagementScreenshotBody", XsrUiOrientation.Vertical, 8);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = new(12, 12, 12, 12);
        var presented = ScreenshotWithCachedThumbnail(item);
        var icon = ContentImage(body, presented, null, imageHeight);
        _screenshotIconEntities[item.Name] = icon;
        if (item.Icon is null && presented.Icon is { } ownedImage) _screenshotOwnedRealizedImages[icon] = ownedImage;
        DesktopLiteralText.Preserve(_shell.Tree, Text(body, item.Name, 12, Ink, 26));
        if (includeTimestamp && ScreenshotTimestamp(item.ModifiedUtcTicks) is { } timestamp)
            DesktopLiteralText.Preserve(_shell.Tree, Text(body, timestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture), 11, Muted, 20));
        else if (includeTimestamp) Text(body, "修改时间未知", 11, Muted, 20);
    }
}
