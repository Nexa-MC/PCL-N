using System.Text;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Bounded user-selected document IO, independent of product settings and services.</summary>
internal static class NativeDocumentTransfer
{
    internal static async Task<string> ReadAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[Math.Min(maximumBytes, 32 * 1024)];
        while (true)
        {
            int count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes - buffer.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new InvalidDataException("Document exceeds its byte budget.");
            buffer.Write(chunk, 0, count);
        }
        byte[] bytes = buffer.ToArray();
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    internal static async Task WriteAsync(string destination, string document, int maximumBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("A fully qualified destination is required.", nameof(destination));
        var encoding = new UTF8Encoding(false, true);
        if (encoding.GetByteCount(document) > maximumBytes) throw new InvalidDataException("Document exceeds its byte budget.");
        byte[] bytes = encoding.GetBytes(document);
        string target = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".nexa-document-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
    }
}
