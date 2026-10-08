using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceLocalDocumentsService
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new() { WriteIndented = true };
    public static async Task<InstanceLocalDocumentsSnapshot> ReadAsync(InstanceLocalDocumentsQuery query, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(query.InstanceDirectory)) throw new InvalidDataException("请选择完整实例目录。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
        var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance)))
            throw new InvalidDataException("请选择 versions 下的实例。");
        RecoveryBlobStore.CheckLinks(instance);
        string root = versions.Parent.FullName;
        List<InstanceLocalDocument> documents = []; long bytesRead = 0; bool complete = true;
        HashSet<string> visited = new(Nexa.Core.PathIdentity.Comparer);
        string? current = Path.Combine(instance, Path.GetFileName(instance) + ".json");
        for (int depth = 0; current is not null && depth < 32; depth++)
        {
            if (!visited.Add(current)) { AddFailure("manifest", current, "继承循环，读取已停止"); current = null; break; }
            var json = await ReadDocument("manifest", current).ConfigureAwait(false);
            if (json is null) { current = null; break; }
            string? reference;
            try { reference = json["inheritsFrom"]?.GetValue<string>() ?? json["jar"]?.GetValue<string>(); }
            catch (InvalidOperationException) { AddFailure("manifest", current, "继承字段形状无效"); current = null; break; }
            if (string.IsNullOrWhiteSpace(reference) || reference == json["id"]?.ToString()
                || reference == Path.GetFileNameWithoutExtension(current)) { current = null; break; }
            if (!MinecraftVersionPaths.IsSafeReference(reference)) { AddFailure("manifest", current, "继承引用无效"); current = null; break; }
            string conventional = Path.Combine(root, "versions", reference, reference + ".json");
            string sibling = Path.Combine(Path.GetDirectoryName(current)!, reference + ".json");
            current = File.Exists(conventional) ? conventional : File.Exists(sibling) ? sibling : conventional;
        }
        if (current is not null) AddFailure("manifest", current, "继承超过 32 层预算");
        string metadata = Path.Combine(instance, MinecraftInstanceMetadataStore.MetadataDirectoryName, MinecraftInstanceMetadataStore.MetadataFileName);
        if (!File.Exists(metadata))
        {
            string legacy = Path.Combine(instance, "PCL", MinecraftInstanceMetadataStore.MetadataFileName);
            if (File.Exists(legacy)) metadata = legacy;
        }
        await ReadDocument("metadata", metadata).ConfigureAwait(false);
        documents.Add(new("lockfile", "", "当前架构未生成独立实例 Lockfile；依赖按 manifest 与完整性元数据解析", [], false));
        return new(instance, DateTimeOffset.UtcNow, documents.AsReadOnly(), complete);

        void AddFailure(string kind, string path, string status)
        { complete = false; documents.Add(new(kind, Path.GetRelativePath(root, path), status, [], false)); }
        async Task<JsonObject?> ReadDocument(string kind, string path)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                RecoveryBlobStore.CheckLinks(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
                long size = input.Length; long modified = File.GetLastWriteTimeUtc(path).Ticks;
                if (size > 2 * 1024 * 1024 || bytesRead + size > 16 * 1024 * 1024) throw new InvalidDataException("文档读取超过预算");
                using var contents = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    bytesRead += read;
                    if (contents.Length + read > 2 * 1024 * 1024 || bytesRead > 16 * 1024 * 1024) throw new InvalidDataException("文档实际读取超过预算");
                    contents.Write(buffer, 0, read);
                }
                if (contents.Length != size || File.GetLastWriteTimeUtc(path).Ticks != modified) throw new IOException("文档读取期间变化");
                var json = JsonNode.Parse(contents.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 48 }) as JsonObject
                    ?? throw new InvalidDataException("文档不是 JSON 对象");
                JsonObject redacted = (JsonObject)json.DeepClone(); RedactNode(redacted);
                string text = LogRedactor.Redact(redacted.ToJsonString(DisplayJsonOptions));
                bool truncated = text.Length > 128 * 1024;
                if (truncated) text = text[..(128 * 1024)];
                documents.Add(new(kind, Path.GetRelativePath(root, path), "本地读取快照；敏感字段已省略", Array.AsReadOnly(text.Split('\n')), truncated));
                return json;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or JsonException or InvalidOperationException or ArgumentException)
            { AddFailure(kind, path, File.Exists(path) ? "无法完整读取有效 JSON；原文件已保留" : "本地文件缺失"); return null; }
        }
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (string key in obj.Select(item => item.Key).ToArray())
                if (SensitiveKey(key)) obj[key] = "<redacted>";
                else if (obj[key] is { } child) RedactNode(child);
                else if (node is JsonArray array)
                    foreach (var item in array) if (item is not null) RedactNode(item);
    }

    private static bool SensitiveKey(string key) => key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("authorization", StringComparison.OrdinalIgnoreCase) || key.Contains("username", StringComparison.OrdinalIgnoreCase)
        || key.Contains("uuid", StringComparison.OrdinalIgnoreCase) || key.Contains("xuid", StringComparison.OrdinalIgnoreCase)
        || key.Equals("accessKey", StringComparison.OrdinalIgnoreCase) || key.Equals("clientId", StringComparison.OrdinalIgnoreCase);
}
