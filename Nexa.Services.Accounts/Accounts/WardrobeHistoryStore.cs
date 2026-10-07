using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexa.Services.Accounts;

/// <summary>Optional, bounded address-only MRU. History I/O failures never reject an appearance mutation.</summary>
public sealed class WardrobeHistoryStore
{
    private const int MaximumEntries = 80;
    private const int FileLimit = 512 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string? _path;
    private IReadOnlyList<AccountWardrobeHistoryEntry> _memory = [];

    public WardrobeHistoryStore(string? dataDirectory = null)
    {
        if (dataDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
            _path = Path.Combine(Path.GetFullPath(dataDirectory), "Appearance", "history.json");
        }
    }

    /// <summary>Stable across texture, display-name and credential updates when a UUID exists.</summary>
    public static string ProfileKey(LaunchProfileView profile) => !string.IsNullOrWhiteSpace(profile.Uuid)
        ? profile.Kind + ":" + profile.Uuid.Replace("-", "", StringComparison.Ordinal).Trim()
        : profile.Kind + ":" + profile.AuthServer + ":" + profile.Username;

    public async ValueTask<IReadOnlyList<AccountWardrobeHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _memory = Normalize((await ReadAsync(cancellationToken).ConfigureAwait(false)).Concat(_memory));
            return _memory;
        }
        finally { Gate.Release(); }
    }

    public async ValueTask RecordAsync(IEnumerable<AccountWardrobeHistoryEntry> newEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newEntries);
        // Materialize a bounded batch before awaiting; later caller edits cannot change the record.
        AccountWardrobeHistoryEntry[] incoming = newEntries.Take(512).ToArray();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _memory = Normalize(incoming.Concat(_memory).Concat(existing));
            if (_path is not null) await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async ValueTask<IReadOnlyList<AccountWardrobeHistoryEntry>> ReadAsync(CancellationToken token)
    {
        if (_path is null) return [];
        try
        {
            using FileStream file = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > FileLimit) return [];
            byte[] bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (file.ReadByte() != -1) return [];
            return JsonSerializer.Deserialize(bytes, WardrobeHistoryJsonContext.Default.ListAccountWardrobeHistoryEntry) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private async ValueTask SaveAsync(CancellationToken token)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_memory.ToList(), WardrobeHistoryJsonContext.Default.ListAccountWardrobeHistoryEntry);
            if (bytes.Length > FileLimit) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _path!, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ReadOnlyCollection<AccountWardrobeHistoryEntry> Normalize(IEnumerable<AccountWardrobeHistoryEntry> entries)
    {
        AccountWardrobeHistoryEntry[] result = entries.Where(entry => entry is not null
                && !string.IsNullOrWhiteSpace(entry.ProfileKey) && entry.ProfileKey.Length <= 512
                && entry.DisplayName is { Length: <= 256 } && entry.Kind is AccountWardrobeTextureKind.Skin or AccountWardrobeTextureKind.Cape
                && WardrobeTextureResolver.SafeAddress(entry.Address) is not null)
            .Select(entry => entry with { Address = WardrobeTextureResolver.SafeAddress(entry.Address)!, LastUsedUtc = entry.LastUsedUtc.ToUniversalTime() })
            .OrderByDescending(entry => entry.LastUsedUtc)
            .DistinctBy(entry => (entry.Kind, entry.Address), HistoryKeyComparer.Instance)
            .Take(MaximumEntries).ToArray();
        return Array.AsReadOnly(result);
    }

    private sealed class HistoryKeyComparer : IEqualityComparer<(AccountWardrobeTextureKind Kind, string Address)>
    {
        internal static HistoryKeyComparer Instance { get; } = new();
        public bool Equals((AccountWardrobeTextureKind Kind, string Address) left, (AccountWardrobeTextureKind Kind, string Address) right)
            => left.Kind == right.Kind && string.Equals(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((AccountWardrobeTextureKind Kind, string Address) value)
            => HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Address));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<AccountWardrobeHistoryEntry>))]
internal sealed partial class WardrobeHistoryJsonContext : JsonSerializerContext;
