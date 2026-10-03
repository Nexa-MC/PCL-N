using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Platform.Updates;

namespace Nexa.Services.Updates;

internal static class ProtectedDeltaExtractor
{
    internal static async Task ExtractAsync(Stream bundle, VerifiedReleasePackage release, string fromVersion,
        IUpdateDirectory source, IUpdateDirectory slot, IUpdateDirectory transaction, CancellationToken token)
    {
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count != 2 || zip.Entries.Count(e => e.FullName == "manifest.json") != 1
            || zip.Entries.Count(e => e.FullName == "data") != 1
            || zip.Entries.Any(e => ((e.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000)))
            throw new InvalidDataException("差分包条目无效。");
        ZipArchiveEntry manifestEntry = zip.GetEntry("manifest.json")!, dataEntry = zip.GetEntry("data")!;
        using var bytes = new MemoryStream();
        using (Stream input = manifestEntry.Open())
            await CopyBounded(input, bytes, manifestEntry.Length, 16 * 1024 * 1024, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        VerifiedUpdateDelta.RequireProperties(root, "schemaVersion", "algorithm", "fromVersion", "version", "rid", "targetSha256", "files");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || VerifiedUpdateDelta.Text(root, "algorithm") != "nexa-file-delta-v1"
            || VerifiedUpdateDelta.Text(root, "fromVersion") != fromVersion || VerifiedUpdateDelta.Text(root, "version") != release.Version
            || VerifiedUpdateDelta.Text(root, "rid") != release.RuntimeId || VerifiedUpdateDelta.Text(root, "targetSha256") != release.Sha256)
            throw new InvalidDataException("差分载荷身份不匹配。");
        JsonElement files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is 0 or > 65536)
            throw new InvalidDataException("差分文件数量无效。");
        using FileStream literals = transaction.CreateFile("delta-data");
        using (Stream input = dataEntry.Open())
            await CopyBounded(input, literals, dataEntry.Length, 4L * 1024 * 1024 * 1024, token).ConfigureAwait(false);
        literals.Flush(true);
        var destinations = new Dictionary<string, IUpdateDirectory>(StringComparer.OrdinalIgnoreCase) { [""] = slot };
        var sources = new Dictionary<string, IUpdateDirectory>(StringComparer.OrdinalIgnoreCase) { [""] = source };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long remaining = 4L * 1024 * 1024 * 1024;
        int operations = 0;
        byte[] buffer = new byte[65536];
        try
        {
            foreach (JsonElement file in files.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                VerifiedUpdateDelta.RequireProperties(file, "path", "size", "sha256", "executable", "chunks");
                string path = VerifiedUpdateDelta.Text(file, "path"), digest = VerifiedUpdateDelta.Text(file, "sha256");
                string[] parts = ValidatePath(path, release.RuntimeId);
                long size = file.GetProperty("size").GetInt64();
                bool executable = file.GetProperty("executable").GetBoolean();
                JsonElement chunks = file.GetProperty("chunks");
                if (!names.Add(path) || destinations.ContainsKey(path) || size is < 0 or > 1024L * 1024 * 1024
                    || size > remaining || !VerifiedUpdateDelta.IsDigest(digest) || chunks.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("差分文件身份或大小无效。");
                remaining -= size;
                IUpdateDirectory parent = Parent(destinations, parts, create: true);
                using FileStream output = parent.CreateFile(parts[^1], publicRead: true, executable);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                FileStream? old = null;
                long actual = 0;
                try
                {
                    foreach (JsonElement chunk in chunks.EnumerateArray())
                    {
                        if (++operations > 262144 || chunk.ValueKind != JsonValueKind.Array || chunk.GetArrayLength() != 3)
                            throw new InvalidDataException("差分块数量或格式无效。");
                        string? kind = chunk[0].GetString();
                        long offset = chunk[1].GetInt64(), count = chunk[2].GetInt64();
                        if (offset < 0 || count <= 0 || count > size - actual)
                            throw new InvalidDataException("差分块输出范围无效。");
                        Stream input;
                        if (kind == "copy")
                        {
                            old ??= Parent(sources, parts, create: false).OpenRead(parts[^1]);
                            input = old;
                        }
                        else if (kind == "data") input = literals;
                        else throw new InvalidDataException("差分块操作无效。");
                        if (offset > input.Length || count > input.Length - offset) throw new InvalidDataException("差分块输入范围无效。");
                        input.Position = offset;
                        long left = count;
                        while (left > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException("差分块被截断。");
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            actual += read; left -= read;
                        }
                    }
                    if (actual != size || Convert.ToHexStringLower(hash.GetHashAndReset()) != digest)
                        throw new InvalidDataException("差分重建文件长度或摘要不匹配。");
                    output.Flush(true);
                }
                finally { old?.Dispose(); }
            }
            string executablePath = release.RuntimeId.StartsWith("osx-", StringComparison.Ordinal)
                ? "Nexa.app/Contents/MacOS/Nexa.Desktop" : release.RuntimeId.StartsWith("win-", StringComparison.Ordinal) ? "Nexa.Desktop.exe" : "Nexa.Desktop";
            if (!names.Contains(executablePath) || !names.Contains(executablePath.Replace("Nexa.Desktop", "Nexa.Jvm.Host", StringComparison.Ordinal)))
                throw new InvalidDataException("差分包缺少启动器或 JVM Host。");
            foreach (var directory in destinations.Values) directory.Flush();
        }
        finally
        {
            foreach (var pair in destinations) if (pair.Key.Length != 0) pair.Value.Dispose();
            foreach (var pair in sources) if (pair.Key.Length != 0) pair.Value.Dispose();
        }

        IUpdateDirectory Parent(Dictionary<string, IUpdateDirectory> directories, string[] parts, bool create)
        {
            string key = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string next = key.Length == 0 ? parts[i] : key + "/" + parts[i];
                if (create && names.Contains(next)) throw new InvalidDataException("差分文件和目录冲突。");
                if (!directories.ContainsKey(next)) directories.Add(next, create
                    ? directories[key].CreateDirectory(parts[i], publicRead: true) : directories[key].OpenDirectory(parts[i]));
                key = next;
            }
            return directories[key];
        }
    }

    internal static string[] ValidatePath(string name, string rid)
    {
        string[] parts = name.Split('/');
        if (name.Length > 4096 || parts.Length > 32 || parts.Any(p => p.Length == 0 || p is "." or ".."
                || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || "<>:\"\\|?*".Contains(c)))
            || (rid.StartsWith("osx-", StringComparison.Ordinal) && parts[0] != "Nexa.app"))
            throw new InvalidDataException("差分文件路径无效。");
        return parts;
    }

    private static async Task CopyBounded(Stream input, Stream output, long declared, long maximum, CancellationToken token)
    {
        if (declared < 0 || declared > maximum) throw new InvalidDataException("差分条目声明大小超限。");
        byte[] buffer = new byte[65536]; long actual = 0; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (read > declared - actual || read > maximum - actual) throw new InvalidDataException("差分条目实际大小超限。");
            actual += read;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (actual != declared) throw new InvalidDataException("差分条目实际长度不匹配。");
    }
}
