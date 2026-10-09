using Nexa.Services.Minecraft.Management;

namespace Nexa.Desktop.Ui;

/// <summary>Cached screenshot geometry; only the requested window becomes retained UI.</summary>
internal sealed class DesktopScreenshotGalleryLayout
{
    internal const double Gap = 12;
    internal const double CardPadding = 12;
    private const double MinimumCardWidth = 220;
    private const double HeadingHeight = 34;

    private DesktopScreenshotGalleryLayout(double availableWidth)
    {
        double width = double.IsFinite(availableWidth) ? Math.Max(1, availableWidth) : MinimumCardWidth;
        ColumnCount = (int)Math.Clamp(Math.Floor((width + Gap) / (MinimumCardWidth + Gap)), 1, 12);
        CardWidth = (width - (ColumnCount - 1) * Gap) / ColumnCount;
    }

    internal int ColumnCount { get; }
    internal double CardWidth { get; }
    internal double Height { get; private set; }
    internal IReadOnlyList<IReadOnlyList<Slot>> Columns { get; private set; } = [];

    internal sealed record Slot(int EntryIndex, InstanceContentEntry? Item, double Top,
        double Height, double ImageHeight, DateTime? HeadingDate)
    {
        internal bool IsHeading => Item is null;
    }

    internal static DesktopScreenshotGalleryLayout Create(IReadOnlyList<InstanceContentEntry> entries,
        double availableWidth, bool timeline)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var layout = new DesktopScreenshotGalleryLayout(availableWidth);
        var columns = Enumerable.Range(0, layout.ColumnCount).Select(_ => new List<Slot>()).ToArray();
        double[] tops = new double[layout.ColumnCount];
        var ordered = entries.Select((item, index) => new IndexedEntry(index, item, LocalDate(item.ModifiedUtcTicks)));
        if (timeline)
            ordered = ordered.OrderByDescending(entry => entry.Date.HasValue)
                .ThenByDescending(entry => entry.Item.ModifiedUtcTicks)
                .ThenBy(entry => entry.Item.Name, StringComparer.OrdinalIgnoreCase);
        bool hasGroup = false;
        DateTime? previousDate = null;
        foreach (var entry in ordered)
        {
            if (timeline && (!hasGroup || entry.Date != previousDate))
            {
                double groupTop = tops.Max();
                columns[0].Add(new(-1, null, groupTop, HeadingHeight, 0, entry.Date));
                Array.Fill(tops, groupTop + HeadingHeight + Gap);
                hasGroup = true;
                previousDate = entry.Date;
            }
            int column = 0;
            for (int candidate = 1; candidate < tops.Length; candidate++)
                if (tops[candidate] < tops[column]) column = candidate;
            double imageWidth = Math.Max(1, layout.CardWidth - CardPadding * 2);
            double imageHeight = entry.Item.Icon is { Width: > 0, Height: > 0 } image
                ? imageWidth * image.Height / image.Width
                : entry.Item is { ImageWidth: { } sourceWidth, ImageHeight: { } sourceHeight }
                    && sourceWidth > 0 && sourceHeight > 0
                    ? imageWidth * sourceHeight / sourceWidth
                    : imageWidth * 9 / 16;
            double height = imageHeight + CardPadding * 2 + 8 + 26 + (timeline ? 28 : 0);
            columns[column].Add(new(entry.Index, entry.Item, tops[column], height, imageHeight, null));
            tops[column] += height + Gap;
        }
        layout.Columns = columns;
        layout.Height = entries.Count == 0 ? 0 : tops.Max() - Gap;
        return layout;
    }

    /// <summary>Binary search each column, then merge visible slots in vertical order.</summary>
    internal IReadOnlyList<IReadOnlyList<Slot>> SelectWindow(double top, double bottom, int maxCards = 1024)
    {
        var selected = Enumerable.Range(0, ColumnCount).Select(_ => new List<Slot>()).ToArray();
        if (!double.IsFinite(top) || !double.IsFinite(bottom) || bottom <= top || maxCards <= 0)
            return selected;
        maxCards = Math.Min(maxCards, 1024);
        var pending = new PriorityQueue<(int Column, int Index), (double Top, int Column)>();
        for (int column = 0; column < ColumnCount; column++)
        {
            var slots = Columns[column];
            int index = FirstEndingAfter(slots, top);
            if (index < slots.Count && slots[index].Top < bottom)
                pending.Enqueue((column, index), (slots[index].Top, column));
        }
        for (int count = 0; count < maxCards && pending.TryDequeue(out var next, out _); count++)
        {
            var slots = Columns[next.Column];
            selected[next.Column].Add(slots[next.Index]);
            int following = next.Index + 1;
            if (following < slots.Count && slots[following].Top < bottom)
                pending.Enqueue((next.Column, following), (slots[following].Top, next.Column));
        }
        return selected;
    }

    private static int FirstEndingAfter(IReadOnlyList<Slot> slots, double top)
    {
        int low = 0, high = slots.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (slots[middle].Top + slots[middle].Height <= top) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static DateTime? LocalDate(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks
        ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().Date : null;

    private readonly record struct IndexedEntry(int Index, InstanceContentEntry Item, DateTime? Date);
}
