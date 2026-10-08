using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nexa.Services.Caching;
using Nexa.Services.Files;

namespace Nexa.Services.Resources;

/// <summary>Resources' metadata codec and disk budget; state ownership and request coalescing live in Common.</summary>
public sealed class ResourceOnlineInformationCache(ISharedStateCache? cache = null, string? directory = null) : IDisposable
{
    internal static readonly TimeSpan CatalogFresh = TimeSpan.FromMinutes(10), CatalogRetain = TimeSpan.FromDays(7);
    internal static readonly TimeSpan IdentityFresh = TimeSpan.FromDays(1), MetadataFresh = TimeSpan.FromDays(7), MetadataRetain = TimeSpan.FromDays(30);
    internal const string StaleNotice = "正在显示缓存资料，后台正在重新连接资源站。";
    private const string Revision = "resource-metadata-v2";
    private const int MaximumRecordBytes = 1024 * 1024, MaximumRecords = 256;
    private const long MaximumDiskBytes = 32L * 1024 * 1024;
    private readonly ISharedStateCache _cache = cache ?? new SharedStateCache();
    private readonly string? _directory = directory is null ? null : Path.GetFullPath(directory);
    // Injected application caches belong to the host. A standalone adapter owns its cache.
    public void Dispose() { if (cache is null && _cache is IDisposable disposable) disposable.Dispose(); }

    internal async Task<T> ReadAsync<T>(string scope, string identity, TimeSpan fresh, TimeSpan retain, long estimatedBytes,
        JsonTypeInfo<ResourceMetadataEnvelope<T>> codec, Func<CancellationToken, Task<T>> read,
        Func<T, bool> positive, Func<T, T> sanitize, Func<T, T> stale, bool refresh = false, bool persistent = true, bool allowStale = true, bool waitForRefresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(scope, identity);
        var policy = new StateCachePolicy(fresh, retain, estimatedBytes);
        if (persistent) await ImportAsync(key, policy, codec, positive, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!refresh && _cache.TryGet<T>(key, out var existing, allowStale: allowStale))
        {
            if (!existing!.IsStale) return Complete(existing.Value);
            if (!waitForRefresh)
            {
                _ = RefreshInBackgroundAsync();
                return Complete(stale(existing.Value));
            }
        }
        try { return Complete(await FetchAsync(token, refresh).ConfigureAwait(false)); }
        catch (Exception error) when (allowStale && !token.IsCancellationRequested && Recoverable(error))
        {
            if (_cache.TryGet<T>(key, out var retained, allowStale: true)) return Complete(stale(retained.Value));
            throw;
        }

        T Complete(T value)
        {
            // WaitAsync may return an already completed task even when its waiter was cancelled.
            token.ThrowIfCancellationRequested();
            return value;
        }
        async Task<T> FetchAsync(CancellationToken waiter, bool force) => await _cache.GetOrCreateAsync(key, policy, async ct =>
        {
            // The caller's cancellation only detaches its waiter. Every network owner has a finite deadline.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            T value = Freeze(await read(deadline.Token).ConfigureAwait(false));
            if (persistent && positive(value)) await PersistAsync(key, value, codec, sanitize, deadline.Token).ConfigureAwait(false);
            return value;
        }, refresh: force, shouldStore: positive, cancellationToken: waiter).ConfigureAwait(false);
        async Task RefreshInBackgroundAsync()
        {
            try { await FetchAsync(CancellationToken.None, true).ConfigureAwait(false); }
            catch (Exception error) when (Recoverable(error)) { }
        }
    }

    internal async Task<StateCacheSnapshot<T>?> ReadSnapshotAsync<T>(string scope, string identity, TimeSpan fresh, TimeSpan retain,
        long estimatedBytes, JsonTypeInfo<ResourceMetadataEnvelope<T>> codec, Func<T, bool> positive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(scope, identity);
        await ImportAsync(key, new(fresh, retain, estimatedBytes), codec, positive, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var snapshot = _cache.TryGet<T>(key, out var value, allowStale: true) ? value : null;
        token.ThrowIfCancellationRequested();
        return snapshot;
    }

    internal async Task SaveAsync<T>(string scope, string identity, T value, TimeSpan fresh, TimeSpan retain, long estimatedBytes,
        JsonTypeInfo<ResourceMetadataEnvelope<T>> codec, Func<T, T> sanitize, CancellationToken token)
    {
        var key = Key(scope, identity);
        T frozen = Freeze(value);
        _cache.Store(key, frozen, new(fresh, retain, estimatedBytes));
        await PersistAsync(key, frozen, codec, sanitize, token).ConfigureAwait(false);
    }

    private async Task ImportAsync<T>(StateCacheKey key, StateCachePolicy policy, JsonTypeInfo<ResourceMetadataEnvelope<T>> codec,
        Func<T, bool> positive, CancellationToken token)
    {
        if (_directory is null || _cache.TryGet<T>(key, out _, allowStale: true)) return;
        try
        {
            string path = Path.Combine(_directory, key.Key + ".json");
            CheckLinks(path);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumRecordBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) return;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
            // The file may be replaced during a read; a bounded read still enforces the budget.
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > MaximumRecordBytes) return;
                bytes.Write(buffer, 0, count);
            }
            var value = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), codec);
            if (value is null || value.Revision != Revision || value.Scope != key.Scope || value.Identity != key.Key || value.Value is null
                || value.StoredAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - value.StoredAt >= policy.RetainFor || !positive(value.Value)) return;
            CheckLinks(path);
            _cache.Store(key, Freeze(value.Value), policy, value.StoredAt);
        }
        catch (Exception error) when (!token.IsCancellationRequested && (Recoverable(error) || error is NullReferenceException or ArgumentException)) { }
    }

    private async Task PersistAsync<T>(StateCacheKey key, T value, JsonTypeInfo<ResourceMetadataEnvelope<T>> codec, Func<T, T> sanitize, CancellationToken token)
    {
        if (_directory is null) return;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ResourceMetadataEnvelope<T>(Revision, key.Scope, key.Key, DateTimeOffset.UtcNow, sanitize(value)), codec);
            if (bytes.Length > MaximumRecordBytes) return;
            CheckLinks(_directory);
            Directory.CreateDirectory(_directory);
            CheckLinks(_directory);
            await using var lease = await AcquireAsync(token).ConfigureAwait(false);
            string path = Path.Combine(_directory, key.Key + ".json");
            CheckLinks(path);
            AtomicFileWriter.Write(path, "resource metadata cache", stream =>
            {
                token.ThrowIfCancellationRequested();
                CheckLinks(path);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                token.ThrowIfCancellationRequested();
                CheckLinks(path);
            }, replaceAttempts: 1, static _ => TimeSpan.Zero);
            Prune(token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && Recoverable(error)) { }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        string path = Path.Combine(_directory!, ".resource-information.lock");
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
    private static bool OwnedFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 64 && name.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }
    private void Prune(CancellationToken token)
    {
        CheckLinks(_directory!);
        var files = Directory.EnumerateFiles(_directory!, "*.json").Where(OwnedFile)
            .Select(path => { CheckLinks(path); return new FileInfo(path); }).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long total = 0;
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            CheckLinks(files[index].FullName);
            total += files[index].Length;
            if (index >= MaximumRecords || total > MaximumDiskBytes) files[index].Delete();
        }
    }
    private static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Resource cache path traverses a link."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    // Closed type dispatch keeps source-generated persistence and immutable ownership free of reflection.
    private static T Freeze<T>(T value) => value switch
    {
        ResourceSearchResult search => (T)(object)(search with { Projects = ReadOnly(search.Projects.Select(FreezeProject)) }),
        ResourceDetail detail => (T)(object)(detail with { Project = FreezeProject(detail.Project), Versions = ReadOnly(detail.Versions.Select(FreezeVersion)) }),
        ResourceVersionMetadata metadata => (T)(object)(metadata with { Version = metadata.Version is null ? null : FreezeVersion(metadata.Version) }),
        ResourceIdentityMetadata identity => (T)(object)(identity with { Files = identity.Files.ToArray() }),
        ResourceIdentityBatchMetadata batch => (T)(object)new ResourceIdentityBatchMetadata(batch.Matches.ToDictionary(item => item.Key,
            item => item.Value with { Files = item.Value.Files.ToArray() }, StringComparer.OrdinalIgnoreCase)),
        _ => value
    };
    private static System.Collections.ObjectModel.ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    private static ResourceProject FreezeProject(ResourceProject project) => project with { Sources = ReadOnly(project.Sources) };
    private static ResourceVersion FreezeVersion(ResourceVersion version) => version with
    { Games = ReadOnly(version.Games), Loaders = ReadOnly(version.Loaders), Dependencies = ReadOnly(version.Dependencies) };

    private static StateCacheKey Key(string scope, string identity) => new("resources-online." + scope,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Revision + "\n" + scope + "\n" + identity))), Revision);
    internal static string Identity(params string[] parts) => string.Concat(parts.Select(part => part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + part));
    internal static string Notice(string? existing) => string.IsNullOrEmpty(existing) ? StaleNotice : StaleNotice + " " + existing;
    internal static bool Recoverable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException;
    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? value : null;
    internal static ResourceProject Sanitize(ResourceProject value) => value with { Website = SafeUrl(value.Website) ?? "", IconUrl = SafeUrl(value.IconUrl) };
    internal static ResourceVersion Sanitize(ResourceVersion value) => value with
    {
        Website = SafeUrl(value.Website) ?? "",
        Changelog = "",
        File = value.File is { } file ? file with { Url = SafeUrl(file.Url) ?? "" } : null
    };
    internal static ResourceDetail Sanitize(ResourceDetail value) => value with
    { Project = Sanitize(value.Project), Versions = value.Versions.Select(Sanitize).ToArray(), Notice = null, IsStale = false };
    internal static ResourceSearchResult Sanitize(ResourceSearchResult value) => value with
    { Projects = value.Projects.Select(Sanitize).ToArray(), Notice = null, IsStale = false };
}

internal sealed record ResourceMetadataEnvelope<T>(string Revision, string Scope, string Identity, DateTimeOffset StoredAt, T Value);
internal sealed record FingerprintMetadata(ResourceInstanceService.Fingerprint? Fingerprint);
internal sealed record ResourceVersionMetadata(ResourceVersion? Version);
internal sealed record ResourceIdentityMetadata(ResourceInstalledFile[] Files, bool Complete, bool IsStale = false);
internal sealed record ResourceIdentityBatchMetadata(Dictionary<string, ResourceIdentityMetadata> Matches);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, MaxDepth = 32)]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceSearchResult>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceDetail>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceVersionMetadata>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceTranslation>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<FingerprintMetadata>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceIdentityMetadata>))]
[JsonSerializable(typeof(ResourceMetadataEnvelope<ResourceIdentityBatchMetadata>))]
internal sealed partial class ResourceMetadataJsonContext : JsonSerializerContext;
