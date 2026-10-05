namespace Nexa.Services.Logging;

/// <summary>Owned read handles and byte limits captured together on the file-sink worker.</summary>
internal sealed class DiskLogSnapshot(IReadOnlyList<DiskLogReadFile> files, bool truncated) : IDisposable
{
    internal IReadOnlyList<DiskLogReadFile> Files { get; } = files;
    internal bool Truncated { get; } = truncated;

    public void Dispose()
    {
        foreach (var file in Files) file.Stream.Dispose();
    }
}

internal readonly record struct DiskLogReadFile(FileStream Stream, long Length, DateTime ModifiedUtc, bool Current);
