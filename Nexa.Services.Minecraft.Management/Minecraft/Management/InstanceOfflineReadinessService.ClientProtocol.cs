using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Management;

public sealed partial class InstanceOfflineReadinessService
{
    internal static async Task<int?> ReadClientProtocolAsync(string instanceDirectory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
            var versions = Directory.GetParent(instance);
            if (!Path.IsPathFullyQualified(instanceDirectory) || versions?.Name != "versions" || versions.Parent is null
                || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance))) return null;
            string root = versions.Parent.FullName;
            var budget = new Budget(); CheckLinks(instance);
            // Nonconventional manifest discovery cannot prove the exact launch selection here.
            string current = Path.Combine(instance, Path.GetFileName(instance) + ".json");
            if (!File.Exists(current)) return null;
            HashSet<string> visited = new(Nexa.Core.PathIdentity.Comparer);
            Manifest? clientManifest = null;
            string? referenceId = null;
            for (int depth = 0; depth < 32; depth++)
            {
                if (!visited.Add(current)) return null;
                var json = await ReadJsonAsync(current, budget, 2 * 1024 * 1024, token).ConfigureAwait(false);
                referenceId ??= json["id"]?.GetValue<string>() ?? Path.GetFileName(instance);
                if (!MinecraftVersionPaths.IsSafeReference(referenceId)) return null;
                string? inherited = json["inheritsFrom"]?.GetValue<string>();
                bool hasParent = !string.IsNullOrWhiteSpace(inherited);
                string? reference = hasParent ? inherited : json["jar"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(reference) || !hasParent && string.Equals(reference, referenceId, StringComparison.OrdinalIgnoreCase))
                { clientManifest = new(current, json); break; }
                if (!MinecraftVersionPaths.IsSafeReference(reference)) return null;
                string standard = Path.Combine(root, "versions", reference, reference + ".json");
                string local = Path.Combine(instance, reference + ".json");
                CheckLinks(standard); CheckLinks(local);
                if (File.Exists(standard)) current = standard;
                else if (File.Exists(local) && !Directory.Exists(Path.Combine(root, "versions", reference))
                    && !ProtocolHasCaseVariant(root, reference, token)) current = local;
                else return null;
                referenceId = reference;
            }
            if (clientManifest is null) return null;
            string id = referenceId!;
            if (!MinecraftVersionPaths.IsSafeReference(id)) return null;
            string? clientPath = ProtocolJarPath(root, instance, id, clientManifest, token);
            if (clientPath is null) return null;
            string metadataPath = Path.Combine(instance, MinecraftInstanceMetadataStore.MetadataDirectoryName, MinecraftInstanceMetadataStore.MetadataFileName);
            if (!File.Exists(metadataPath)) metadataPath = Path.Combine(instance, "PCL", MinecraftInstanceMetadataStore.MetadataFileName);
            MinecraftInstanceMetadata metadata = new();
            if (File.Exists(metadataPath))
            {
                var metadataJson = await ReadJsonAsync(metadataPath, budget, 256 * 1024, token).ConfigureAwait(false);
                metadata = JsonSerializer.Deserialize(metadataJson.ToJsonString(), MinecraftJsonContext.Default.MinecraftInstanceMetadata) ?? throw new IOException("实例元数据无效。");
                if (metadata.SchemaVersion != 1) return null;
            }
            var download = clientManifest.Json["downloads"]?["client"];
            string? patch = metadata.CorePatchSha256.Length == 64 ? metadata.CorePatchSha256 : null;
            var fact = await VerifyAsync(root, new("client", clientPath, patch is null ? Number(download?["size"]) : null,
                download?["sha1"]?.GetValue<string>(), patch), budget, token).ConfigureAwait(false);
            if (fact.State != OfflineArtifactState.Verified) return null;
            CheckLinks(clientPath);
            await using var file = new FileStream(clientPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (!await ProtocolZipBudgetAsync(file, token).ConfigureAwait(false)) return null;
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 16384) return null;
            ZipArchiveEntry? entry = null;
            foreach (var candidate in archive.Entries)
                if (candidate.FullName == "version.json")
                { if (entry is not null) return null; entry = candidate; }
            if (entry is null || entry.Length is <= 0 or > 65536) return null;
            await using var input = entry.Open(); byte[] bytes = new byte[65537]; int length = 0, read;
            while (length < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false)) > 0) length += read;
            if (length != entry.Length || length > 65536) return null;
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count(property => property.NameEquals("protocol_version")) != 1
                || !document.RootElement.TryGetProperty("protocol_version", out var protocol)
                || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out int value) || value < 0) return null;
            foreach (var stamp in budget.Stamps)
            {
                token.ThrowIfCancellationRequested(); CheckLinks(stamp.Key); var info = new FileInfo(stamp.Key);
                if (!info.Exists || info.Length != stamp.Value.Bytes || info.LastWriteTimeUtc.Ticks != stamp.Value.Modified) return null;
            }
            return Nexa.Core.PathIdentity.Comparer.Equals(clientPath, ProtocolJarPath(root, instance, id, clientManifest, token)) ? value : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or ArgumentException or OverflowException or FormatException)
        { return null; }
    }

    private static bool ProtocolHasCaseVariant(string root, string reference, CancellationToken token)
    {
        int count = 0;
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(root, "versions")))
        {
            token.ThrowIfCancellationRequested();
            if (++count > 1024 || string.Equals(Path.GetFileName(directory), reference, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? ProtocolJarPath(string root, string instance, string reference, Manifest manifest, CancellationToken token)
    {
        string local = Path.Combine(instance, reference + ".jar");
        if (File.Exists(local)) return local;
        string versions = Path.Combine(root, "versions"); CheckLinks(versions);
        List<string> directories = [];
        foreach (string directory in Directory.EnumerateDirectories(versions))
        { token.ThrowIfCancellationRequested(); if (directories.Count >= 1024) return null; directories.Add(directory); }
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        int filesRead = 0;
        string? Named(string directory, string name)
        {
            CheckLinks(directory); string exact = Path.Combine(directory, name);
            if (File.Exists(exact)) return exact;
            List<string> files = [];
            foreach (string path in Directory.EnumerateFiles(directory))
            { token.ThrowIfCancellationRequested(); if (++filesRead > 16384) throw new IOException("客户端 JAR 发现超过预算。"); files.Add(path); }
            return files.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }
        string? preferred = directories.FirstOrDefault(path => Path.GetFileName(path) == reference)
            ?? directories.FirstOrDefault(path => string.Equals(Path.GetFileName(path), reference, StringComparison.OrdinalIgnoreCase));
        string? result = preferred is null ? null : Named(preferred, reference + ".jar");
        result ??= Named(instance, reference + ".jar");
        foreach (string directory in directories) { if (result is not null) break; result = Named(directory, reference + ".jar"); }
        string manifestDirectory = Path.GetDirectoryName(manifest.Path)!;
        result ??= Named(manifestDirectory, Path.GetFileNameWithoutExtension(manifest.Path) + ".jar");
        string? id = manifest.Json["id"]?.GetValue<string>();
        if (result is null && MinecraftVersionPaths.IsSafeReference(id)) result = Named(manifestDirectory, id + ".jar");
        return result ?? Named(manifestDirectory, Path.GetFileName(manifestDirectory) + ".jar");
    }

    private static async Task<bool> ProtocolZipBudgetAsync(FileStream file, CancellationToken token)
    {
        if (file.Length < 22) return false;
        byte[] tail = new byte[(int)Math.Min(file.Length, 65557)]; file.Position = file.Length - tail.Length;
        await file.ReadExactlyAsync(tail, token).ConfigureAwait(false);
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) != 0x06054b50
                || index + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20)) != tail.Length) continue;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 10));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 12));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index + 16));
            if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 4)) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 6)) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 8)) != count || count > 16384 || size > 4 * 1024 * 1024
                || (long)offset + size != file.Length - tail.Length + index) return false;
            byte[] central = new byte[(int)size]; file.Position = offset; await file.ReadExactlyAsync(central, token).ConfigureAwait(false);
            int position = 0;
            for (int entry = 0; entry < count; entry++)
            {
                token.ThrowIfCancellationRequested();
                if (central.Length - position < 46 || BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(position)) != 0x02014b50) return false;
                int entryBytes = 46 + BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(position + 28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(position + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(position + 32));
                if (entryBytes > central.Length - position) return false; position += entryBytes;
            }
            return position == central.Length;
        }
        return false;
    }
}
