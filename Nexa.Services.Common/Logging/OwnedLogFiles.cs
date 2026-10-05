using System.Globalization;

namespace Nexa.Services.Logging;

/// <summary>Only this sink's active file and generated archive namespace are disk-log inputs.</summary>
internal sealed class OwnedLogFiles(string currentPath)
{
    internal const int MaximumArchiveCount = 32;
    internal const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const int MaximumDirectoryEntries = 4096;
    internal string CurrentPath { get; } = Path.GetFullPath(currentPath);
    internal string DirectoryPath { get; } = Path.GetDirectoryName(Path.GetFullPath(currentPath))!;
    private readonly string _prefix = Path.GetFileNameWithoutExtension(currentPath) + "-archive-";

    internal void EnsureDirectory()
    {
        EnsureNoDirectoryLinks();
        Directory.CreateDirectory(DirectoryPath);
        EnsureNoDirectoryLinks();
    }

    internal void EnsureNoDirectoryLinks()
    {
        for (string? path = DirectoryPath; path is not null; path = Path.GetDirectoryName(path))
            if (new DirectoryInfo(path).LinkTarget is not null
                || Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The configured log directory contains a link.");
    }

    internal static void EnsureRegularFile(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || Directory.Exists(path))
            throw new IOException("A disk log must be a regular file.");
    }

    internal string NewArchivePath() => Path.Combine(DirectoryPath,
        _prefix + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N") + ".log");

    internal List<OwnedLogFile> Archives()
    {
        EnsureNoDirectoryLinks();
        if (!Directory.Exists(DirectoryPath)) return [];
        List<OwnedLogFile> files = [];
        int examined = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(DirectoryPath))
        {
            if (++examined > MaximumDirectoryEntries) throw new IOException("The log directory exceeds the scan budget.");
            string name = Path.GetFileName(path);
            if (!IsArchiveName(name)) continue;
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
            files.Add(new(path, file.LastWriteTimeUtc, file.Length));
        }
        files.Sort(static (left, right) =>
        {
            int time = left.ModifiedUtc.CompareTo(right.ModifiedUtc);
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });
        return files;
    }

    private bool IsArchiveName(string name)
    {
        if (!name.StartsWith(_prefix, StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal)) return false;
        var identity = name.AsSpan(_prefix.Length, name.Length - _prefix.Length - 4);
        return identity.Length == 51 && identity[18] == '-'
            && DateTime.TryParseExact(identity[..18], "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && Guid.TryParseExact(identity[19..], "N", out _);
    }

    internal DiskLogSnapshot CaptureSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var archives = Archives();
        var selected = archives.TakeLast(MaximumArchiveCount).ToList();
        EnsureRegularFile(CurrentPath);
        var current = new FileInfo(CurrentPath);
        if (current.Exists) selected.Add(new(CurrentPath, current.LastWriteTimeUtc, current.Length));
        if (selected.Sum(static file => file.Length) > MaximumArchiveBytes + FileLogSink.MaximumFileBytes)
            throw new InvalidDataException("Disk log inputs exceed the export budget.");
        List<DiskLogReadFile> opened = [];
        try
        {
            foreach (var file in selected)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoDirectoryLinks(); EnsureRegularFile(file.Path);
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
                try
                {
                    opened.Add(new(stream, Math.Min(stream.Length, file.Length), file.ModifiedUtc,
                        StringComparer.Ordinal.Equals(file.Path, CurrentPath)));
                }
                catch { stream.Dispose(); throw; }
            }
            token.ThrowIfCancellationRequested();
            return new(opened, archives.Count > MaximumArchiveCount);
        }
        catch
        {
            foreach (var file in opened) file.Stream.Dispose();
            throw;
        }
    }

    internal void Prune(int? retentionDays)
    {
        var archives = Archives();
        long bytes = archives.Sum(static file => file.Length);
        int count = archives.Count;
        DateTime cutoff = DateTime.UtcNow.AddDays(-(retentionDays ?? 90));
        foreach (var archive in archives)
        {
            bool aged = retentionDays is not null && archive.ModifiedUtc < cutoff;
            if (!aged && count <= MaximumArchiveCount && bytes <= MaximumArchiveBytes) continue;
            EnsureRegularFile(archive.Path);
            File.Delete(archive.Path);
            count--; bytes -= archive.Length;
        }
    }
}

internal readonly record struct OwnedLogFile(string Path, DateTime ModifiedUtc, long Length);
