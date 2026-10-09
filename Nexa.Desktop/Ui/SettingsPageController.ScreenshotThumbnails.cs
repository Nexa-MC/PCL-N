using Nexa.Core.Media;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    // Retain a small encoded-media working set independently of the management scan budget.
    // Only realized cards request missing originals, with two admitted reads at a time.
    private const long ScreenshotThumbnailByteLimit = 64 * 1024 * 1024;
    private readonly Dictionary<XsrUiEntityId, PngImage> _screenshotOwnedRealizedImages = [];
    private readonly Dictionary<string, XsrUiEntityId> _screenshotIconEntities = new(StringComparer.Ordinal);
    private readonly Dictionary<InstanceScreenshotQuery, (PngImage Image, long Use)> _screenshotThumbnails = [];
    private readonly Dictionary<InstanceScreenshotQuery, Task<XsrResult<InstanceScreenshot>>> _screenshotThumbnailReads = [];
    private readonly HashSet<InstanceScreenshotQuery> _screenshotThumbnailFailures = [];
    private CancellationTokenSource? _screenshotThumbnailStop;
    private long _screenshotThumbnailUse;
    internal long ScreenshotThumbnailOwnedBytes => ScreenshotOwnedImages().Sum(image => (long)image.Bytes.Length);
    internal long ScreenshotThumbnailReservedBytes => _screenshotThumbnailReads.Keys.Sum(file => Math.Max(0, file.ExpectedSize));

    private InstanceContentEntry ScreenshotWithCachedThumbnail(InstanceContentEntry item)
    {
        if (item.Icon is not null || _instance is null || item.Size is not { } size) return item;
        var file = new InstanceScreenshotQuery(_instance, item.Name, size, item.ModifiedUtcTicks);
        if (!_screenshotThumbnails.TryGetValue(file, out var cached)) return item;
        _screenshotThumbnails[file] = (cached.Image, ++_screenshotThumbnailUse);
        return item with { Icon = cached.Image };
    }

    private void UpdateScreenshotThumbnails()
    {
        if (_selected != "screenshots" || !_visible || _instance is null || _contentSnapshot is not { } snapshot
            || !_queries.TryResolve(InstanceScreenshotContract.Read, out var route)) return;
        _screenshotThumbnailStop ??= new();
        foreach (var entry in _screenshotThumbnailReads.Where(entry => entry.Value.IsCompleted).ToArray())
        {
            _screenshotThumbnailReads.Remove(entry.Key);
            if (PendingQuery.Succeeded(entry.Value))
            {
                var image = entry.Value.Result.Value!.Image;
                // The typed file read promises this exact identity and encoded size. Replacing
                // its reservation must not admit an unexpectedly larger carrier.
                if (image.Bytes.Length != entry.Key.ExpectedSize)
                { _screenshotThumbnailFailures.Add(entry.Key); continue; }
                _screenshotThumbnails[entry.Key] = (image, ++_screenshotThumbnailUse);
                while (_screenshotThumbnails.Count > 32)
                {
                    var oldest = _screenshotThumbnails.MinBy(item => item.Value.Use);
                    _screenshotThumbnails.Remove(oldest.Key);
                }
                if (_instance == entry.Key.InstanceDirectory && snapshot.Entries.Any(item => SameScreenshot(item, entry.Key))
                    && _screenshotIconEntities.TryGetValue(entry.Key.Name, out var entity) && _shell.Tree.IsAlive(entity))
                {
                    _shell.Tree.GetComponent<XsrUiImage>(entity)!.Raster = new(image,
                        [new(new(0, 0, image.Width, image.Height), new(0, 0, 1, 1))])
                    { FitToBounds = true };
                    _screenshotOwnedRealizedImages[entity] = image;
                    _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
                }
            }
            else _screenshotThumbnailFailures.Add(entry.Key);
        }
        if (_screenshotThumbnailReads.Count >= 2 || _screenshotPreview.IsAssigned) return;
        long availableBytes = ScreenshotThumbnailByteLimit - ScreenshotThumbnailOwnedBytes - ScreenshotThumbnailReservedBytes;
        var realizedImages = new HashSet<PngImage>(_screenshotOwnedRealizedImages.Values, ReferenceEqualityComparer.Instance);
        foreach (var item in _screenshotVisibleSlots?.SelectMany(column => column).Select(slot => slot.Item).Where(item => item is not null) ?? [])
        {
            if (item!.Icon is not null || item.IsDirectory || item.Size is not { } size || !item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
            var file = new InstanceScreenshotQuery(_instance, item.Name, size, item.ModifiedUtcTicks);
            // A realized card still owns its current carrier after LRU eviction. Re-reading
            // that image on every frame would starve later visible cards when there are >32.
            if (_screenshotIconEntities.TryGetValue(item.Name, out var realized) && _shell.Tree.IsAlive(realized)
                && _shell.Tree.GetComponent<XsrUiImage>(realized)?.Raster is not null) continue;
            if (_screenshotThumbnails.ContainsKey(file) || _screenshotThumbnailReads.ContainsKey(file) || _screenshotThumbnailFailures.Contains(file)) continue;
            if (size < 0 || size > 16 * 1024 * 1024) { _screenshotThumbnailFailures.Add(file); continue; }
            while (availableBytes < size)
            {
                // Cache entries outside the realized window may release their owned bytes.
                // Preserve every current card even when it was already evicted from the LRU.
                var candidates = _screenshotThumbnails.Where(cached => !realizedImages.Contains(cached.Value.Image)).ToArray();
                if (candidates.Length == 0) break;
                var oldest = candidates.MinBy(cached => cached.Value.Use);
                _screenshotThumbnails.Remove(oldest.Key);
                availableBytes = ScreenshotThumbnailByteLimit - ScreenshotThumbnailOwnedBytes - ScreenshotThumbnailReservedBytes;
            }
            if (availableBytes < size) continue;
            var task = _queries.QueryAsync<InstanceScreenshotQuery, InstanceScreenshot>(route, file, cancellationToken: _screenshotThumbnailStop.Token).AsTask();
            _screenshotThumbnailReads.Add(file, task); availableBytes -= size; ObserveTransfer(task);
            if (_screenshotThumbnailReads.Count >= 2) break;
        }
    }

    private HashSet<PngImage> ScreenshotOwnedImages()
    {
        // Refresh/page retirement can leave the old retained controls alive until their
        // replacement is arranged. Keep accounting for their carriers until destruction.
        foreach (var entry in _screenshotOwnedRealizedImages.Where(entry => !_shell.Tree.IsAlive(entry.Key)
            || _shell.Tree.GetComponent<XsrUiImage>(entry.Key)?.Raster?.Image != entry.Value).ToArray())
            _screenshotOwnedRealizedImages.Remove(entry.Key);
        var images = new HashSet<PngImage>(ReferenceEqualityComparer.Instance);
        foreach (var cached in _screenshotThumbnails.Values) images.Add(cached.Image);
        foreach (var realized in _screenshotOwnedRealizedImages.Values) images.Add(realized);
        return images;
    }

    private void CancelScreenshotThumbnails()
    {
        _screenshotThumbnailStop?.Cancel(); _screenshotThumbnailStop?.Dispose(); _screenshotThumbnailStop = null;
        _screenshotThumbnailReads.Clear(); _screenshotThumbnailFailures.Clear(); _screenshotIconEntities.Clear();
        _screenshotLayout = null; _screenshotLayoutEntries = null; _screenshotVisibleSlots = null;
    }
}
