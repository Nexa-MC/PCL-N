using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private bool _screenshotTimeline;
    private int _screenshotTimelinePage;

    private static DateTime? ScreenshotTimestamp(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : null;

    private void BuildScreenshotTimeline(InstanceContentSnapshot snapshot)
    {
        int columns = Math.Max(1, (int)((_shell.Renderer.Viewport.Width - 130) / 250));
        int pages = Math.Max(1, (snapshot.Entries.Count + 24) / 25); _screenshotTimelinePage = Math.Clamp(_screenshotTimelinePage, 0, pages - 1);
        if (_contentWindowStart == _screenshotTimelinePage && _contentWindowCount == snapshot.Entries.Count && _contentColumns == columns) return;
        _contentWindowStart = _screenshotTimelinePage; _contentWindowCount = snapshot.Entries.Count; _contentColumns = columns;
        _contentIconEntities.Clear(); foreach (var action in _contentActions) _managementActions.Remove(action); _contentActions.Clear();
        foreach (var child in _shell.Tree.Children(_contentList).ToArray()) _shell.Tree.Destroy(child);
        Text(_contentList, "按文件实际修改日期分组", 12, Muted, 30);
        if (snapshot.Entries.Count == 0) Text(_contentList, "没有匹配的截图。", 13, Muted, 36);
        var ordered = snapshot.Entries.OrderByDescending(item => item.ModifiedUtcTicks).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var day in ordered.Skip(_screenshotTimelinePage * 25).Take(25).GroupBy(item => ScreenshotTimestamp(item.ModifiedUtcTicks)?.Date))
        {
            if (day.Key is { } date) DesktopLiteralText.Preserve(_shell.Tree, Text(_contentList, date.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.CurrentCulture), 15, Ink, 34, 600));
            else Text(_contentList, "修改日期未知", 15, Ink, 34, 600);
            var entries = day.ToArray();
            for (int start = 0; start < entries.Length; start += columns)
            {
                var row = Stack(_contentList, "ScreenshotTimelineRow", XsrUiOrientation.Horizontal, 12);
                _shell.Tree.GetComponent<XsrUiElement>(row)!.Height = 224;
                foreach (var item in entries.Skip(start).Take(columns)) BuildScreenshotCard(row, item, includeTimestamp: true);
            }
        }
        if (pages > 1) ManagementButton(_contentList, $"{_screenshotTimelinePage + 1}/{pages} · " + _shell.Renderer.LocalizeText("下一页"), () =>
        { _screenshotTimelinePage = (_screenshotTimelinePage + 1) % pages; _contentWindowStart = -1; BuildSections(true); }, 144);
    }
}
