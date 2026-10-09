using System.Buffers.Binary;
using Nexa.Core.Media;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ScreenshotWaterfallLayoutKeepsRatiosAndBoundedWindows()
    {
        InstanceContentEntry Screenshot(string name, int width, int height, long ticks = 0)
        {
            byte[] bytes = new byte[33];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13);
            "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
            return new(name, false, bytes.Length) { Icon = PngImage.TryCreatePreview(bytes), ModifiedUtcTicks = ticks };
        }
        var entries = new[]
        {
            Screenshot("portrait.png", 300, 900), Screenshot("wide.png", 1600, 900),
            Screenshot("square.png", 400, 400), Screenshot("short.png", 1200, 200),
            new InstanceContentEntry("unreadable.png", false, 10)
        };
        var narrow = DesktopScreenshotGalleryLayout.Create(entries, 300, false);
        AssertEqual(1, narrow.ColumnCount);
        AssertEqual(string.Join(',', entries.Select(item => item.Name)),
            string.Join(',', narrow.Columns[0].Select(slot => slot.Item!.Name)));
        var layout = DesktopScreenshotGalleryLayout.Create(entries, 700, false);
        AssertEqual(3, layout.ColumnCount);
        AssertClose(700d, layout.CardWidth * layout.ColumnCount + DesktopScreenshotGalleryLayout.Gap * (layout.ColumnCount - 1));
        var slots = layout.Columns.SelectMany(column => column).ToDictionary(slot => slot.EntryIndex);
        double imageWidth = layout.CardWidth - DesktopScreenshotGalleryLayout.CardPadding * 2;
        AssertClose(imageWidth * 3, slots[0].ImageHeight);
        AssertClose(imageWidth * 900 / 1600, slots[1].ImageHeight);
        AssertClose(imageWidth, slots[2].ImageHeight);
        AssertClose(imageWidth * 9 / 16, slots[4].ImageHeight);
        AssertEqual(3, layout.Columns[1][1].EntryIndex);
        AssertTrue(slots[3].Top < slots[0].Height);
        foreach (var column in layout.Columns)
            for (int index = 1; index < column.Count; index++)
                AssertClose(column[index - 1].Top + column[index - 1].Height + DesktopScreenshotGalleryLayout.Gap, column[index].Top);
        AssertClose(slots.Values.Max(slot => slot.Top + slot.Height), layout.Height);
        AssertEqual(12, DesktopScreenshotGalleryLayout.Create(entries, 20_000, false).ColumnCount);
        var metadata = DesktopScreenshotGalleryLayout.Create(
            [new("header-only.png", false, 10) { ImageWidth = 300, ImageHeight = 900 },
                new("invalid-header.png", false, 10) { ImageWidth = 0, ImageHeight = 900 },
                entries[1] with { ImageWidth = 300, ImageHeight = 900 }], 700, false);
        AssertClose(imageWidth * 3, metadata.Columns[0][0].ImageHeight);
        AssertClose(imageWidth * 9 / 16, metadata.Columns[1][0].ImageHeight);
        AssertClose(imageWidth * 900 / 1600, metadata.Columns[2][0].ImageHeight);

        var many = Enumerable.Range(0, 20_000).Select(index => new InstanceContentEntry($"{index}.png", false, 10)).ToArray();
        var large = DesktopScreenshotGalleryLayout.Create(many, 1100, false);
        double windowTop = large.Height * .72, windowBottom = windowTop + 800;
        var visible = large.SelectWindow(windowTop, windowBottom).SelectMany(column => column).ToArray();
        AssertTrue(visible.Length is > 0 and <= 1024);
        AssertTrue(visible.All(slot => slot.Top < windowBottom && slot.Top + slot.Height > windowTop));
        var expected = large.Columns.SelectMany(column => column)
            .Where(slot => slot.Top < windowBottom && slot.Top + slot.Height > windowTop)
            .Select(slot => slot.EntryIndex).Order().ToArray();
        AssertEqual(string.Join(',', expected), string.Join(',', visible.Select(slot => slot.EntryIndex).Order()));
        AssertEqual(1024, large.SelectWindow(0, large.Height + 1).Sum(column => column.Count));
        AssertEqual(5, large.SelectWindow(0, large.Height + 1, 5).Sum(column => column.Count));
        AssertEqual(0, large.SelectWindow(large.Height + 1, large.Height + 100).Sum(column => column.Count));

        var veryWideImages = Enumerable.Range(0, 20_000).Select(index => new InstanceContentEntry($"short-{index}.png", false, 33)
        { ImageWidth = 4096, ImageHeight = 1 }).ToArray();
        foreach (var viewport in new[] { (Width: 3840d, Height: 2160d), (Width: 2160d, Height: 3840d) })
        {
            var highResolution = DesktopScreenshotGalleryLayout.Create(veryWideImages, viewport.Width - 148, false);
            double top = highResolution.Height * .5, bottom = top + viewport.Height;
            var actualWindow = highResolution.SelectWindow(top - 320, bottom + 320).SelectMany(column => column).ToArray();
            var actualViewport = actualWindow.Where(slot => slot.Top < bottom && slot.Top + slot.Height > top).Select(slot => slot.EntryIndex).Order().ToArray();
            var expectedViewport = highResolution.Columns.SelectMany(column => column)
                .Where(slot => slot.Top < bottom && slot.Top + slot.Height > top).Select(slot => slot.EntryIndex).Order().ToArray();
            AssertTrue(actualWindow.Length <= 1024);
            AssertEqual(string.Join(',', expectedViewport), string.Join(',', actualViewport));
            var first = highResolution.Columns[0][0];
            AssertClose((highResolution.CardWidth - DesktopScreenshotGalleryLayout.CardPadding * 2) / 4096, first.ImageHeight);
        }

        var newer = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var older = newer.AddDays(-1);
        var timeline = DesktopScreenshotGalleryLayout.Create(
            [Screenshot("9999-12-31.png", 400, 400, older.Ticks), Screenshot("1990-01-01.png", 800, 450, newer.Ticks),
                Screenshot("same-day.png", 400, 400, newer.AddHours(-1).Ticks), new("unknown.png", false, 1)], 700, true);
        var headings = timeline.Columns[0].Where(slot => slot.IsHeading).ToArray();
        AssertEqual(3, headings.Length);
        AssertEqual<DateTime?>(newer.ToLocalTime().Date, headings[0].HeadingDate);
        AssertEqual<DateTime?>(older.ToLocalTime().Date, headings[1].HeadingDate);
        AssertEqual<DateTime?>(null, headings[2].HeadingDate);
        var firstGroup = timeline.Columns.SelectMany(column => column)
            .Where(slot => !slot.IsHeading && slot.Top < headings[1].Top).ToArray();
        AssertEqual("1990-01-01.png,same-day.png", string.Join(',', firstGroup.OrderByDescending(slot => slot.Item!.ModifiedUtcTicks).Select(slot => slot.Item!.Name)));
        AssertTrue(firstGroup.All(slot => slot.Top + slot.Height + DesktopScreenshotGalleryLayout.Gap <= headings[1].Top));
        AssertClose(28d, timeline.Columns[0].Single(slot => slot.EntryIndex == 1).Height
            - DesktopScreenshotGalleryLayout.Create([Screenshot("same.png", 800, 450)], 700, false).Columns[0][0].Height);
        AssertClose(0d, DesktopScreenshotGalleryLayout.Create([], 700, false).Height);
        AssertEqual(1, DesktopScreenshotGalleryLayout.Create(entries, double.NaN, false).ColumnCount);
    }
}
