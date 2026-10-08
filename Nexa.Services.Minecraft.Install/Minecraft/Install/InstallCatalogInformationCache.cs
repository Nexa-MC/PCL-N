using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Caching;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Typed catalog presentation snapshots; installer task receipts retain their own authority.</summary>
public sealed class InstallCatalogInformationCache
{
    internal static readonly StateCachePolicy Policy = new(TimeSpan.FromMinutes(10), TimeSpan.FromDays(7), 1024 * 1024);
    internal const string StaleNotice = "正在显示缓存版本资料，后台正在重新连接安装来源。";
    private const int Schema = 1, MaximumRecordBytes = 1024 * 1024, MaximumRecords = 32;
    private const long MaximumDiskBytes = 16L * 1024 * 1024;
    private readonly string _directory;
    private readonly TimeProvider _time;

    public InstallCatalogInformationCache(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _time = timeProvider ?? TimeProvider.System;
    }

    internal async Task<StateCacheSnapshot<IReadOnlyList<InstallCatalogVersion>>?> ReadAsync(StateCacheKey key, CancellationToken token)
    {
        if (!KnownRequest(key)) return null;
        try
        {
            string path = RecordPath(key);
            RecoveryBlobStore.CheckLinks(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 16384, true);
            if (input.Length is <= 0 or > MaximumRecordBytes) return null;
            byte[] bytes = new byte[(int)input.Length];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (input.ReadByte() != -1) return null;
            var envelope = JsonSerializer.Deserialize(bytes, InstallCatalogInformationJsonContext.Default.InstallCatalogInformationEnvelope);
            DateTimeOffset now = _time.GetUtcNow();
            if (envelope is null || envelope.Schema != Schema || envelope.Scope != key.Scope || envelope.Identity != key.Key
                || envelope.SourcePolicy != key.Revision || envelope.StoredAt > now || now - envelope.StoredAt >= Policy.RetainFor
                || !Positive(envelope.Versions)) return null;
            return new(Freeze(envelope.Versions), envelope.StoredAt, now - envelope.StoredAt >= Policy.FreshFor);
        }
        catch (Exception error) when (!token.IsCancellationRequested && Recoverable(error)) { return null; }
    }

    internal async Task SaveAsync(StateCacheKey key, IReadOnlyList<InstallCatalogVersion> versions, CancellationToken token)
    {
        if (!KnownRequest(key) || !Positive(versions)) return;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new InstallCatalogInformationEnvelope(
                Schema, key.Scope, key.Key, key.Revision, _time.GetUtcNow(), versions.ToArray()),
                InstallCatalogInformationJsonContext.Default.InstallCatalogInformationEnvelope);
            if (bytes.Length > MaximumRecordBytes) return;
            RecoveryBlobStore.CheckLinks(_directory);
            Directory.CreateDirectory(_directory);
            RecoveryBlobStore.CheckLinks(_directory);
            await using var lease = await AcquireAsync(token).ConfigureAwait(false);
            string path = RecordPath(key);
            RecoveryBlobStore.CheckLinks(path);
            AtomicFileWriter.Write(path, "install catalog cache", stream =>
            {
                token.ThrowIfCancellationRequested();
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                token.ThrowIfCancellationRequested();
                RecoveryBlobStore.CheckLinks(path);
            }, replaceAttempts: 1, static _ => TimeSpan.Zero);
            Prune(token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && Recoverable(error)) { }
    }

    internal static bool Positive(IReadOnlyList<InstallCatalogVersion>? versions)
    {
        if (versions is null || versions.Count is < 1 or > 4096) return false;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            if (version is null || !SafeIdentity(version.Id) || !ids.Add(version.Id) || !SafeText(version.Detail, 512)
                || version.Warning is not null || !SafeText(version.ForgeRequirement, 256) || !SafeText(version.FabricRequirement, 256)
                || version.Downloads is { Count: > 16 }) return false;
            foreach (var download in version.Downloads ?? [])
                if (download is null || !SafeIdentity(download.FileName) || !SafeText(download.Source, 64)
                    || string.IsNullOrWhiteSpace(download.Source) || download.Size is < 0 or > RecoveryBlobStore.MaxFileBytes
                    || download.Sha1 is { } hash && (hash.Length != 40 || !hash.All(char.IsAsciiHexDigit))
                    || download.Url is not { IsAbsoluteUri: true } url || url.Scheme != Uri.UriSchemeHttps
                    || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsoluteUri.Length > 8192)
                    return false;
        }
        return true;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<InstallCatalogVersion> Freeze(IEnumerable<InstallCatalogVersion> versions) =>
        Array.AsReadOnly(versions.Select(version => version with
        { Downloads = version.Downloads is null ? null : Array.AsReadOnly(version.Downloads.ToArray()) }).ToArray());

    internal static System.Collections.ObjectModel.ReadOnlyCollection<InstallCatalogVersion> Normalize(IReadOnlyList<InstallCatalogVersion> versions) =>
        Freeze(versions.Where(version => version is not null && !string.IsNullOrWhiteSpace(version.Id))
            .DistinctBy(version => version.Id, StringComparer.Ordinal));

    private static bool SafeIdentity(string? value) => value is { Length: > 0 and <= 256 } && MinecraftVersionPaths.IsSafeReference(value)
        && !value.Contains('|') && !value.Any(char.IsControl);
    private static bool SafeText(string? value, int maximum) => value is null || value.Length <= maximum && !value.Any(char.IsControl);
    private static bool KnownRequest(StateCacheKey key)
    {
        if (key.Scope != "minecraft.install-catalog" || key.Key is not { Length: > 0 and <= 512 }
            || key.Revision is not { Length: > 0 and <= 1024 }
            || key.Revision.Any(char.IsControl)) return false;
        int separator = key.Key.IndexOf('|');
        if (separator < 0 || key.Key.IndexOf('|', separator + 1) >= 0) return false;
        string game = key.Key[..separator], loader = key.Key[(separator + 1)..];
        return game.Length == 0 && loader.Length == 0 || SafeIdentity(game)
            && Enum.TryParse<InstallLoader>(loader, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == loader;
    }
    private string RecordPath(StateCacheKey key) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Scope + "\n" + key.Key + "\n" + key.Revision))) + ".json");
    private static bool OwnedFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 64 && name.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }
    private void Prune(CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(_directory);
        var files = Directory.EnumerateFiles(_directory, "*.json").Where(OwnedFile)
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long total = 0;
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(files[index].FullName);
            total += files[index].Length;
            if (index >= MaximumRecords || total > MaximumDiskBytes) files[index].Delete();
        }
    }
    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        string path = Path.Combine(_directory, ".catalog.lock");
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
    private static bool Recoverable(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or ArgumentException or InvalidOperationException or NullReferenceException;
}

internal sealed record InstallCatalogInformationEnvelope(int Schema, string Scope, string Identity,
    string SourcePolicy, DateTimeOffset StoredAt, InstallCatalogVersion[] Versions);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, MaxDepth = 16,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstallCatalogInformationEnvelope))]
internal sealed partial class InstallCatalogInformationJsonContext : JsonSerializerContext;
