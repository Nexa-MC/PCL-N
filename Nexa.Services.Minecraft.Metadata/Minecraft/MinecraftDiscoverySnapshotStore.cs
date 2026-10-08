using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Core;

namespace Nexa.Services.Minecraft;

internal sealed record MinecraftDiscoveryDisplayState(string DisplayName, string[] Tags, string Group,
    bool IsStarred, string ModpackVersion, int LaunchCount, bool InstanceIsolation);
internal sealed record MinecraftDiscoveryCacheInstance(string Id, string VersionId,
    MinecraftVersionDescriptor Version, MinecraftDiscoveryDisplayState Display);
internal sealed record MinecraftDiscoveryCacheDocument(int SchemaVersion, string RootDirectory,
    string FileIdentity, DateTimeOffset CapturedAt, string PayloadSha256, MinecraftDiscoveryCacheInstance[] Instances);

/// <summary>Disposable display/cache hints, separate from user-owned instance configuration.</summary>
internal sealed class MinecraftDiscoverySnapshotStore(string directory)
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumInstances = 4096;
    private const int MaximumRoots = 32;
    private const long MaximumTotalBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private readonly string _directory = PathIdentity.Normalize(directory);

    internal async ValueTask<MinecraftDiscoveryCacheDocument?> LoadAsync(string root, CancellationToken token)
    {
        try
        {
            string path = GetPath(root); CheckLinks(path);
            if (!File.Exists(path)) return null;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumBytes) return null;
            byte[] bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (stream.ReadByte() != -1) return null;
            MinecraftDiscoveryCacheDocument? document = JsonSerializer.Deserialize(bytes,
                MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheDocument);
            return document is not null && Validate(document, root) ? document : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        { return null; }
    }

    internal async ValueTask SaveAsync(string root, string identity, IReadOnlyList<MinecraftInstanceDescriptor> instances,
        CancellationToken token)
    {
        if (instances.Count > MaximumInstances || instances.Any(instance => instance.MetadataError is not null)) return;
        MinecraftDiscoveryCacheInstance[] payload = instances.Select(instance => new MinecraftDiscoveryCacheInstance(
            instance.Id, instance.VersionId, instance.Version, new(instance.Metadata.DisplayName,
                instance.Metadata.Tags.ToArray(), instance.Metadata.Group, instance.Metadata.IsStarred,
                instance.Metadata.ModpackVersion, instance.Metadata.LaunchCount, instance.Metadata.InstanceIsolation))).ToArray();
        MinecraftDiscoveryCacheDocument document = new(1, root, identity, DateTimeOffset.UtcNow, Hash(payload), payload);
        if (!Validate(document, root)) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheDocument);
        if (bytes.Length > MaximumBytes) return;
        string path = GetPath(root); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            CheckLinks(path); Directory.CreateDirectory(_directory); CheckLinks(_directory);
            await using var lease = await AcquireAsync(token).ConfigureAwait(false);
            // A bounded LRU disk budget; pruning affects hints only, never the game installation.
            var retained = new DirectoryInfo(_directory).EnumerateFiles("*.json")
                .Where(file => OwnedFile(file.Name) && !PathIdentity.Comparer.Equals(file.FullName, path))
                .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long total = bytes.Length; int count = 1;
            foreach (FileInfo file in retained)
            {
                token.ThrowIfCancellationRequested();
                CheckLinks(file.FullName);
                if (++count > MaximumRoots || total + file.Length > MaximumTotalBytes) File.Delete(file.FullName);
                else total += file.Length;
            }
            CheckLinks(temporary);
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            CheckLinks(path); File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { }
        finally
        {
            try { CheckLinks(temporary); File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        string path = Path.Combine(_directory, ".minecraft-discovery.lock");
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            CheckLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 199) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }

    private static bool OwnedFile(string name) => name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal)
        && name[..64].All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static MinecraftInstanceDiscoverySnapshot ToDisplaySnapshot(MinecraftDiscoveryCacheDocument document) =>
        new(document.RootDirectory, document.CapturedAt, Array.AsReadOnly(document.Instances.Select(instance =>
            new MinecraftInstanceDescriptor(instance.Id, instance.Version.DirectoryPath, instance.VersionId, instance.Version,
                new MinecraftInstanceMetadata
                {
                    DisplayName = instance.Display.DisplayName,
                    Tags = instance.Display.Tags.ToArray(),
                    Group = instance.Display.Group,
                    IsStarred = instance.Display.IsStarred,
                    ModpackVersion = instance.Display.ModpackVersion,
                    LaunchCount = instance.Display.LaunchCount,
                    InstanceIsolation = instance.Display.InstanceIsolation
                })).ToArray()));

    private static bool Validate(MinecraftDiscoveryCacheDocument document, string root)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (document.SchemaVersion != 1 || !PathIdentity.Comparer.Equals(document.RootDirectory, root)
            || document.CapturedAt > now || now - document.CapturedAt > MaximumAge
            || document.FileIdentity is not { Length: 64 } || !document.FileIdentity.All(char.IsAsciiHexDigit)
            || document.Instances is null || document.Instances.Length > MaximumInstances
            || document.PayloadSha256 is not { Length: 64 }) return false;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var instance in document.Instances)
        {
            if (instance is null || !MinecraftVersionPaths.IsSafeReference(instance.Id) || !ids.Add(instance.Id)
                || !MinecraftVersionPaths.IsSafeReference(instance.VersionId) || instance.Version is not { } version
                || version.Id != instance.VersionId || version.Classification.Id is null || version.Classification.Id.Length > 180
                || version.Classification.Type is null || version.Classification.Type.Length > 128
                || version.MainClass is { Length: > 32768 } || version.InheritsFrom is { Length: > 180 }
                || !Contained(root, version.DirectoryPath) || !Contained(version.DirectoryPath, version.JsonPath)
                || version.JarPath is { } jar && !Contained(root, jar)
                || !PathIdentity.Comparer.Equals(version.DirectoryPath, Path.Combine(root, "versions", instance.Id))
                || instance.Display is not { } display || display.DisplayName is null || display.DisplayName.Length > 32768
                || display.Group is null || display.Group.Length > 32768 || display.ModpackVersion is null || display.ModpackVersion.Length > 32768
                || display.Tags is null || display.Tags.Length > 32 || display.Tags.Any(tag => tag is null || tag.Length > 64)
                || !Enum.IsDefined(version.Kind) || !Enum.IsDefined(version.Classification.Category)) return false;
        }
        return string.Equals(document.PayloadSha256, Hash(document.Instances), StringComparison.Ordinal);
    }

    private static bool Contained(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        string prefix = PathIdentity.Normalize(root) + Path.DirectorySeparatorChar;
        return PathIdentity.Normalize(path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string Hash(MinecraftDiscoveryCacheInstance[] payload) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(payload, MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheInstanceArray)));

    private string GetPath(string root)
    {
        string identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    internal static void CheckLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Discovery cache path traverses a link."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MinecraftDiscoveryCacheDocument))]
[JsonSerializable(typeof(MinecraftDiscoveryCacheInstance[]))]
internal sealed partial class MinecraftDiscoveryCacheJsonContext : JsonSerializerContext;
