using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexa.Services.Minecraft;

public sealed class MinecraftInstanceMetadataStore
{
    public const string MetadataDirectoryName = "Nexa";
    public const string MetadataFileName = "InstanceMetadata.json";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(GetPathComparer());
    private readonly string _metadataDirectoryName = MetadataDirectoryName;
    private readonly string _metadataFileName = MetadataFileName;

    public string GetMetadataPath(string instanceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        return Path.Combine(Path.GetFullPath(instanceDirectory), _metadataDirectoryName, _metadataFileName);
    }

    public async Task<MinecraftInstanceMetadata> LoadAsync(string instanceDirectory, CancellationToken cancellationToken = default)
    {
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<MinecraftInstanceMetadataDisplaySnapshot> LoadForDisplayAsync(string instanceDirectory,
        CancellationToken cancellationToken = default)
    {
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { return new(await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false), null); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { return new(new(), "实例信息不可用：" + error.Message); }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(string instanceDirectory, MinecraftInstanceMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(metadata), metadata.SchemaVersion, "Only schema 1 can be saved.");
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false);
            await SaveCoreAsync(path, metadata, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<MinecraftInstanceMetadata> UpdateAsync(string instanceDirectory, Func<MinecraftInstanceMetadata, MinecraftInstanceMetadata> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MinecraftInstanceMetadata current = await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false);
            MinecraftInstanceMetadata next = update(current) ?? throw new InvalidOperationException("The metadata callback returned null.");
            if (next.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(update), next.SchemaVersion, "Only schema 1 can be saved.");
            await SaveCoreAsync(path, next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task<MinecraftInstanceMetadata> UpdateValidatedAsync(string instanceDirectory,
        Func<MinecraftInstanceMetadata, CancellationToken, ValueTask<MinecraftInstanceMetadata>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        string path = GetMetadataPath(instanceDirectory);
        SemaphoreSlim gate = _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MinecraftInstanceMetadata current = await LoadCoreAsync(path, cancellationToken).ConfigureAwait(false);
            MinecraftInstanceMetadata next = await update(current, cancellationToken).ConfigureAwait(false);
            if (next.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
                throw new ArgumentOutOfRangeException(nameof(update), next.SchemaVersion, "Only schema 1 can be saved.");
            await SaveCoreAsync(path, next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally { gate.Release(); }
    }

    private static async Task<MinecraftInstanceMetadata> LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        CheckLinks(path);
        if (!File.Exists(path))
        {
            string legacy = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!, "PCL", MetadataFileName);
            CheckLinks(legacy);
            if (!File.Exists(legacy)) return new MinecraftInstanceMetadata();
            path = legacy; // Read old installations; all new writes use the Nexa directory.
        }
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 512 * 1024) throw new InvalidDataException("实例信息超过 512 KiB，原文件已保留。");
            using var contents = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 512 * 1024 - contents.Length + 1)), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (contents.Length + read > 512 * 1024) throw new InvalidDataException("实例信息实际大小超过 512 KiB，原文件已保留。");
                contents.Write(buffer, 0, read);
            }
            MinecraftInstanceMetadata metadata = JsonSerializer.Deserialize(contents.ToArray(), MinecraftJsonContext.Default.MinecraftInstanceMetadata)
                ?? throw new InvalidDataException("实例信息不是有效对象，原文件已保留。");
            ValidateShape(metadata); return metadata;
        }
        catch (JsonException error) { throw new InvalidDataException("实例信息 JSON 损坏，原文件已保留。", error); }
    }

    internal static void ValidateShape(MinecraftInstanceMetadata metadata)
    {
        if (metadata.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
            throw new InvalidDataException("不支持现有实例信息 schema，原文件已保留。");
        string?[] texts = [metadata.Identity, metadata.DisplayName, metadata.Group, metadata.Notes, metadata.Description,
            metadata.LogoPath, metadata.ModpackVersion, metadata.ModpackProjectId, metadata.WindowTitle, metadata.CustomInfo,
            metadata.SelectedJavaPath, metadata.AuthServerAddress, metadata.AuthRegisterAddress, metadata.AuthServerDisplayName,
            metadata.ServerToEnter, metadata.ServerExpectedGameVersion, metadata.ServerExpectedLoader, metadata.JvmArguments,
            metadata.GameArguments, metadata.ClasspathHead, metadata.WrapperCommand, metadata.PreLaunchCommand];
        if (texts.Any(value => value is null || value.Length > 32768) || metadata.ServerLoginRequirement is < 0 or > 3
            || metadata.Tags is null || metadata.Tags.Length > 32 || metadata.Tags.Any(value => value is null || value.Length > 64)
            || metadata.ServerRequiredMods is null || metadata.ServerRequiredMods.Length > 128 || metadata.ServerRequiredMods.Any(value => value is null || value.Length > 128))
            throw new InvalidDataException("实例信息受管字段形状无效，原文件已保留。");
    }

    private static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("实例信息路径经过链接，原文件已保留。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task SaveCoreAsync(string path, MinecraftInstanceMetadata metadata, CancellationToken cancellationToken)
    {
        ValidateShape(metadata); CheckLinks(path);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(metadata, MinecraftJsonContext.Default.MinecraftInstanceMetadata);
        if (payload.Length > 512 * 1024) throw new InvalidDataException("实例信息写入超过 512 KiB，原文件已保留。");
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Metadata path has no parent.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    Converters = [typeof(MinecraftInstanceMetadataJsonConverter)])]
[JsonSerializable(typeof(MinecraftInstanceMetadata))]
internal sealed partial class MinecraftJsonContext : JsonSerializerContext;

// Keep source-generated init-only construction from overwriting omitted member defaults under AOT.
// The separate wire context has no converter, so both default serialization and the final read are
// generated paths with no recursion or reflection fallback.
internal sealed class MinecraftInstanceMetadataJsonConverter : JsonConverter<MinecraftInstanceMetadata>
{
    private static readonly byte[] Defaults = JsonSerializer.SerializeToUtf8Bytes(new MinecraftInstanceMetadata(),
        MinecraftInstanceMetadataWireJsonContext.Default.MinecraftInstanceMetadata);

    public MinecraftInstanceMetadataJsonConverter() { }

    public override MinecraftInstanceMetadata Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Instance metadata must be a JSON object.");
        using var defaults = JsonDocument.Parse(Defaults);
        var supplied = new HashSet<string>(options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var payload = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                supplied.Add(property.Name);
                property.WriteTo(writer);
            }
            foreach (var property in defaults.RootElement.EnumerateObject())
                if (!supplied.Contains(property.Name)) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return JsonSerializer.Deserialize(payload.WrittenSpan, MinecraftInstanceMetadataWireJsonContext.Default.MinecraftInstanceMetadata)
            ?? throw new JsonException("Instance metadata must be a JSON object.");
    }

    public override void Write(Utf8JsonWriter writer, MinecraftInstanceMetadata value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, MinecraftInstanceMetadataWireJsonContext.Default.MinecraftInstanceMetadata);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(MinecraftInstanceMetadata))]
internal sealed partial class MinecraftInstanceMetadataWireJsonContext : JsonSerializerContext;
