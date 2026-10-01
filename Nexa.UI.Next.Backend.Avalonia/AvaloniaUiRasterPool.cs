using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Nexa.Core.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Bounded dynamic bitmap ownership. Pixel charge is not native/GPU telemetry.</summary>
internal sealed class AvaloniaUiRasterPool(long byteBudget, int entryBudget)
{
    internal static AvaloniaUiRasterPool Shared { get; } = new(64L * 1024 * 1024, 512);
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly LinkedList<Entry> _idle = [];
    private readonly long _byteBudget = byteBudget > 0 ? byteBudget : throw new ArgumentOutOfRangeException(nameof(byteBudget));
    private readonly int _entryBudget = entryBudget > 0 ? entryBudget : throw new ArgumentOutOfRangeException(nameof(entryBudget));
    private long _bytes;
    private long _revision;
    private long _decodeAttempts;
    private long _disposedBitmaps;

    internal long Revision { get { lock (_gate) return _revision; } }
    internal (long Bytes, int Entries, int Leases, long DecodeAttempts, long DisposedBitmaps) Retained
    {
        get
        {
            lock (_gate)
            {
                int leases = 0;
                foreach (Entry entry in _entries.Values) leases += entry.References;
                return (_bytes, _entries.Count, leases, _decodeAttempts, _disposedBitmaps);
            }
        }
    }

    internal Lease? Acquire(PngImage image, bool fit, int width, out bool capacityBlocked, out long revision)
    {
        lock (_gate)
        {
            capacityBlocked = false;
            revision = _revision;
            Key key = new(image.Key, fit, width);
            if (_entries.TryGetValue(key, out Entry? retained))
            {
                if (retained.Idle is { } idle) { _idle.Remove(idle); retained.Idle = null; }
                retained.References++;
                return new(this, retained);
            }

            // DecodeToWidth can round height upward. Reserve that rectangle before entering
            // the decoder; all callers share this gate, including distinct native surfaces.
            if (width < 1 || width > image.Width) return null;
            int height = fit ? Math.Min(image.Height, (int)Math.Ceiling((double)image.Height * width / image.Width) + 1) : image.Height;
            long reserved = (long)width * height * 8;
            if (reserved > _byteBudget)
            {
                capacityBlocked = true;
                return null;
            }
            while (_bytes + reserved > _byteBudget || _entries.Count >= _entryBudget)
            {
                if (_idle.First is not { } oldest)
                {
                    capacityBlocked = true;
                    revision = _revision;
                    return null;
                }
                Remove(oldest.Value);
            }

            Bitmap? bitmap = null;
            try
            {
                if (!MemoryMarshal.TryGetArray(image.Bytes, out ArraySegment<byte> bytes)) return null;
                using MemoryStream stream = new(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
                _decodeAttempts++;
                bitmap = fit ? Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality) : new(stream);
                int actualWidth = bitmap.PixelSize.Width, actualHeight = bitmap.PixelSize.Height;
                if (actualWidth != width || actualHeight < 1 || actualHeight > height
                    || (fit ? actualWidth > 1024 || actualHeight > 1024 : actualHeight != image.Height)) return null;
                Entry entry = new(key, bitmap, (long)actualWidth * actualHeight * 8);
                _entries.Add(key, entry);
                _bytes += entry.Bytes;
                bitmap = null;
                return new(this, entry);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
            { return null; }
            finally
            {
                if (bitmap is not null) { bitmap.Dispose(); _disposedBitmaps++; }
                revision = _revision;
            }
        }
    }

    internal void TrimIdle()
    {
        lock (_gate)
            while (_idle.First is { } oldest) Remove(oldest.Value);
    }

    private void Remove(Entry entry)
    {
        _idle.Remove(entry.Idle!);
        _entries.Remove(entry.Identity);
        _bytes -= entry.Bytes;
        _revision++;
        entry.Bitmap.Dispose();
        _disposedBitmaps++;
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.References != 0) return;
            entry.Idle = _idle.AddLast(entry);
            _revision++;
        }
    }

    internal readonly record struct Key(string Identity, bool Fit, int Width);
    internal sealed class Entry(Key identity, Bitmap bitmap, long bytes)
    {
        internal Key Identity { get; } = identity;
        internal Bitmap Bitmap { get; } = bitmap;
        internal long Bytes { get; } = bytes;
        internal int References = 1;
        internal LinkedListNode<Entry>? Idle;
    }

    internal sealed class Lease(AvaloniaUiRasterPool owner, Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        internal Bitmap Bitmap => _entry?.Bitmap ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is { } released) owner.Release(released);
        }
    }
}
