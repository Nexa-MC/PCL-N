using System.Formats.Tar;
using System.IO.Compression;
using Nexa.Platform.Updates;

namespace Nexa.Services.Updates;

internal static class ProtectedUpdateExtractor
{
    internal static async Task ExtractAsync(Stream package, string rid, IUpdateDirectory slot, CancellationToken token)
    {
        var directories = new Dictionary<string, IUpdateDirectory>(StringComparer.Ordinal) { [""] = slot };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long remaining = 4L * 1024 * 1024 * 1024;
        int entries = 0;
        try
        {
            if (rid.StartsWith("win-", StringComparison.Ordinal))
            {
                using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
                foreach (var entry in zip.Entries)
                {
                    if (((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000 or 0x4000))
                        throw new InvalidDataException("更新包包含链接或特殊文件。");
                    bool directory = entry.FullName.EndsWith('/');
                    using var input = directory ? null : entry.Open();
                    await Entry(entry.FullName, directory, entry.Length, input, executable: false).ConfigureAwait(false);
                }
            }
            else
            {
                using var gzip = new GZipStream(package, CompressionMode.Decompress, leaveOpen: true);
                using var tar = new TarReader(gzip, leaveOpen: true);
                while (await tar.GetNextEntryAsync(copyData: false, token).ConfigureAwait(false) is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException("更新包包含链接或特殊文件。");
                    string name = entry.Name;
                    // Linux archives have a single known wrapper. macOS preserves the signed bundle.
                    if (rid.StartsWith("linux-", StringComparison.Ordinal))
                    {
                        if (name.TrimEnd('/') == "Nexa") continue;
                        if (!name.StartsWith("Nexa/", StringComparison.Ordinal)) throw new InvalidDataException("更新包根目录无效。");
                        name = name[5..];
                    }
                    await Entry(name, entry.EntryType == TarEntryType.Directory, entry.Length, entry.DataStream,
                        (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0).ConfigureAwait(false);
                }
            }
            string executable = rid.StartsWith("osx-", StringComparison.Ordinal) ? "Nexa.app/Contents/MacOS/Nexa.Desktop" : rid.StartsWith("win-", StringComparison.Ordinal) ? "Nexa.Desktop.exe" : "Nexa.Desktop";
            string host = executable.Replace("Nexa.Desktop", "Nexa.Jvm.Host", StringComparison.Ordinal);
            if (!files.Contains(executable) || !files.Contains(host)) throw new InvalidDataException("更新包缺少启动器或 JVM Host。");
            foreach (var directory in directories.Values) directory.Flush();
        }
        finally { foreach (var pair in directories) if (pair.Key.Length != 0) pair.Value.Dispose(); }

        async Task Entry(string name, bool directory, long length, Stream? input, bool executable)
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 65536 || length is < 0 or > 1024L * 1024 * 1024) throw new InvalidDataException("更新包条目超限。");
            name = name.TrimEnd('/');
            string[] parts = name.Split('/');
            if (name.Length > 4096 || parts.Length > 32 || parts.Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"\\|?*".Contains(c))))
                throw new InvalidDataException("更新包路径无效。");
            if (rid.StartsWith("osx-", StringComparison.Ordinal) && parts[0] != "Nexa.app") throw new InvalidDataException("更新包应用目录无效。");
            string parent = "";
            int count = directory ? parts.Length : parts.Length - 1;
            for (int i = 0; i < count; i++)
            {
                string path = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (files.Contains(path)) throw new InvalidDataException("更新包目录与文件冲突。");
                if (!directories.ContainsKey(path)) directories.Add(path, directories[parent].CreateDirectory(parts[i], publicRead: true));
                parent = path;
            }
            if (directory) return;
            if (!files.Add(name) || directories.ContainsKey(name) || input is null) throw new InvalidDataException("更新包条目重复或为空。");
            using FileStream output = directories[parent].CreateFile(parts[^1], publicRead: true, executable);
            byte[] buffer = new byte[65536];
            long actual = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (read > length - actual || read > remaining) throw new InvalidDataException("更新包实际展开大小超限。");
                actual += read; remaining -= read;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (actual != length) throw new InvalidDataException("更新包条目实际长度不匹配。");
            output.Flush(true);
        }
    }
}
