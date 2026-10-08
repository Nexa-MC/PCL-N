using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceFileWorkspaceService
{
    private const int Limit = 1024 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".json", ".cfg", ".properties", ".toml", ".txt", ".yaml", ".yml", ".conf", ".ini" };

    public static async Task<InstanceFileListing> ListAsync(InstanceFileListQuery query, CancellationToken token = default)
    {
        var (snapshot, path) = await ResolveAsync(query.InstanceDirectory, query.Area, query.RelativeDirectory, token).ConfigureAwait(false);
        List<InstanceFileEntry> entries = []; bool complete = true;
        if (Directory.Exists(path))
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                if (entries.Count == 1000) { complete = false; break; }
                if (entry.Name.StartsWith(".nexa-edit-", StringComparison.Ordinal)) continue;
                entries.Add(new(entry.Name, entry is DirectoryInfo, entry is FileInfo file ? file.Length : null, entry.LastWriteTimeUtc.Ticks));
            }
        return new(snapshot.InstanceDirectory, query.Area, query.RelativeDirectory, path,
            entries.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(), complete);
    }

    public static async Task<InstanceFileDocument> ReadAsync(InstanceFileReadQuery query, CancellationToken token = default)
    {
        if (query.RelativePath.Length == 0) throw new InvalidDataException("请选择文本文件。");
        var (_, path) = await ResolveAsync(query.InstanceDirectory, query.Area, query.RelativePath, token).ConfigureAwait(false);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != query.ExpectedSize || info.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks || info.Length > Limit)
            throw new IOException("文件已变化或超过 1 MiB 文本预算。");
        byte[] bytes = new byte[(int)info.Length + 1];
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            int count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
            if (count != info.Length) throw new IOException("读取期间文件变化。");
            Array.Resize(ref bytes, count);
        }
        info.Refresh();
        if (!info.Exists || info.Length != query.ExpectedSize || info.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks) throw new IOException("读取期间文件变化。");
        string encoding = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? "utf8-bom"
            : bytes.AsSpan().StartsWith(new byte[] { 255, 254 }) ? "utf16-le" : bytes.AsSpan().StartsWith(new byte[] { 254, 255 }) ? "utf16-be" : "utf8";
        var codec = Codec(encoding); int prefix = encoding == "utf8-bom" ? 3 : encoding.StartsWith("utf16", StringComparison.Ordinal) ? 2 : 0;
        string text = codec.GetString(bytes, prefix, bytes.Length - prefix);
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))) throw new InvalidDataException("文件包含二进制数据，不能作为文本编辑。");
        return new(query, text, Convert.ToHexString(SHA256.HashData(bytes)), encoding, query.Area == "config" && TextExtensions.Contains(Path.GetExtension(path)));
    }

    public static async Task<InstanceFileSavePreview> PreviewAsync(InstanceFileSavePreviewQuery query, CancellationToken token = default)
    {
        var actual = await ReadAsync(query.Original.File, token).ConfigureAwait(false);
        if (!actual.Editable || actual.Revision != query.Original.Revision) throw new IOException("配置已变化或不能编辑，请重新读取。");
        byte[] bytes = Encode(query.Text, actual.Encoding);
        if (Path.GetExtension(actual.File.RelativePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            using (JsonDocument.Parse(query.Text, new() { MaxDepth = 128 })) { }
        return new(actual, query.Text, Convert.ToHexString(SHA256.HashData(bytes)), actual.File.ExpectedSize, bytes.Length, Lines(actual.Text), Lines(query.Text));
    }

    public static async Task<XsrResult> SaveAsync(InstanceFileSaveCommand command, XsrStateStore store, CancellationToken token = default)
    {
        string? stage = null;
        try
        {
            var request = command.Preview;
            var (snapshot, path) = await ResolveAsync(request.Original.File.InstanceDirectory, request.Original.File.Area, request.Original.File.RelativePath, token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            InstanceContentTrash.RejectRunning(snapshot, store, token);
            var preview = await PreviewAsync(new(request.Original, request.Text), token).ConfigureAwait(false);
            if (request.Revision != preview.Revision || request.UpdatedBytes != preview.UpdatedBytes || request.PreviousBytes != preview.PreviousBytes)
                throw new InvalidDataException("保存内容不属于此预览。");
            string parent = Path.GetDirectoryName(path)!; stage = Path.Combine(parent, ".nexa-edit-" + Guid.NewGuid().ToString("N"));
            RecoveryBlobStore.CheckLinks(stage); byte[] bytes = Encode(preview.Text, preview.Original.Encoding);
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            if ((await ReadAsync(preview.Original.File, token).ConfigureAwait(false)).Revision != preview.Original.Revision) throw new IOException("配置在预览后再次变化。");
            string backup = path + ".nexa-backup"; RecoveryBlobStore.CheckLinks(path); RecoveryBlobStore.CheckLinks(backup);
            InstanceContentTrash.RejectRunning(snapshot, store, token); token.ThrowIfCancellationRequested(); File.Replace(stage, path, backup, true);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally { if (stage is not null && File.Exists(stage)) File.Delete(stage); }
    }

    private static async Task<(InstanceManagementSnapshot Snapshot, string Path)> ResolveAsync(string instance, string area, string relative, CancellationToken token)
    {
        if (area is not ("config" or "logs" or "crash-reports" or "other") || relative.Length > 4096 || relative.Contains('\\')
            || relative.Length > 0 && (relative.Split('/').Length > 16 || relative.Split('/').Any(part => !MinecraftVersionPaths.IsSafeReference(part))))
            throw new InvalidDataException("文件路径超出此实例的受限工作区。");
        var snapshot = await InstanceManagementService.ReadAsync(new(instance), token).ConfigureAwait(false);
        string path = Path.Combine(snapshot.GameDirectory, area == "other" ? "" : area, relative.Replace('/', Path.DirectorySeparatorChar)); RecoveryBlobStore.CheckLinks(path);
        return (snapshot, path);
    }
    private static Encoding Codec(string kind) => kind switch
    {
        "utf8" or "utf8-bom" => new UTF8Encoding(false, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        _ => throw new InvalidDataException("文件编码不支持编辑。")
    };
    private static byte[] Encode(string text, string kind)
    {
        if (text.Length > Limit || text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))) throw new InvalidDataException("配置内容无效或超过预算。");
        var codec = Codec(kind); byte[] bytes = codec.GetBytes(text);
        byte[] prefix = kind switch { "utf8-bom" => [239, 187, 191], "utf16-le" => [255, 254], "utf16-be" => [254, 255], _ => [] };
        if (bytes.Length + prefix.Length > Limit) throw new InvalidDataException("保存内容超过 1 MiB 预算。");
        return prefix.Concat(bytes).ToArray();
    }
    private static int Lines(string text) => text.Length == 0 ? 0 : 1 + text.Count(character => character == '\n');
}
