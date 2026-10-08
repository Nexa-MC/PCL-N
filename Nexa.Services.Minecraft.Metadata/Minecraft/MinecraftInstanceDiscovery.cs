using Nexa.Core;
using Nexa.Services.Caching;
using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft;

/// <summary>
/// Discovers installed version/instance directories without loading UI state. Metadata is read
/// through the same atomic store used by instance commands, so discovery never observes a
/// partially written document.
/// </summary>
public sealed class MinecraftInstanceDiscovery : IMinecraftInstanceSnapshotSource
{
    private const string LogModuleName = "InstanceScan";
    private const string CatalogScope = "minecraft.installed-catalog";
    private sealed record Catalog(IReadOnlyList<MinecraftVersionDescriptor> Versions, string? Identity, string? CompleteIdentity = null);
    private readonly LogService? _log;
    private readonly MinecraftVersionDiscovery _versionDiscovery;
    private readonly MinecraftInstanceMetadataStore _metadataStore;
    private readonly ISharedStateCache? _cache;
    private readonly MinecraftDiscoverySnapshotStore? _snapshots;

    public MinecraftInstanceDiscovery(LogService? log = null, MinecraftVersionDiscovery? versionDiscovery = null,
        MinecraftInstanceMetadataStore? metadataStore = null)
    {
        _log = log; _versionDiscovery = versionDiscovery ?? new MinecraftVersionDiscovery();
        _metadataStore = metadataStore ?? new MinecraftInstanceMetadataStore();
    }

    public MinecraftInstanceDiscovery(ISharedStateCache sharedCache, string? snapshotDirectory = null, LogService? log = null,
        MinecraftVersionDiscovery? versionDiscovery = null, MinecraftInstanceMetadataStore? metadataStore = null)
        : this(log, versionDiscovery, metadataStore)
    {
        _cache = sharedCache ?? throw new ArgumentNullException(nameof(sharedCache));
        _snapshots = snapshotDirectory is null ? null : new MinecraftDiscoverySnapshotStore(snapshotDirectory);
    }

    public async ValueTask<MinecraftInstanceDiscoverySnapshot?> LoadSnapshotAsync(string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        string root = NormalizeRoot(minecraftRootDirectory);
        if (_snapshots is null) return null;
        MinecraftDiscoveryCacheDocument? document = await _snapshots.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        return document is null ? null : MinecraftDiscoverySnapshotStore.ToDisplaySnapshot(document);
    }

    public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> RefreshAsync(string minecraftRootDirectory,
        CancellationToken cancellationToken = default) => DiscoverCoreAsync(minecraftRootDirectory, true, cancellationToken);

    public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(
        string minecraftRootDirectory,
        CancellationToken cancellationToken = default) => DiscoverCoreAsync(minecraftRootDirectory, false, cancellationToken);

    private async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverCoreAsync(
        string minecraftRootDirectory, bool refresh, CancellationToken cancellationToken)
    {
        string root = NormalizeRoot(minecraftRootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        using LogOperation? operation = _log?.BeginOperation(LogModuleName, "DiscoverInstances", $"root={minecraftRootDirectory}");
        string? currentInstance = null;
        try
        {
            operation?.Stage("discover_versions");
            Catalog catalog = await GetCatalogAsync(root, refresh, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<MinecraftVersionDescriptor> versions = catalog.Versions;
            _log?.Write(LogLevel.RealTime, LogModuleName,
                $"Version directories discovered root={minecraftRootDirectory} count={versions.Count}");
            operation?.Stage("read_instance_metadata", $"count={versions.Count}");
            List<MinecraftInstanceDescriptor> result = new(versions.Count);
            int iconBudget = 16 * 1024 * 1024;
            foreach (MinecraftVersionDescriptor version in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = Path.GetFileName(version.DirectoryPath);
                if (!MinecraftVersionPaths.IsSafeReference(id)) continue;
                currentInstance = id;
                var metadataRead = await _metadataStore.LoadForDisplayAsync(version.DirectoryPath, cancellationToken).ConfigureAwait(false);
                MinecraftInstanceMetadata metadata = metadataRead.Metadata;
                if (metadataRead.Error is not null) _log?.Warn(LogModuleName, $"Instance metadata unavailable instance={id}");
                Nexa.Core.Media.PngImage? icon = null;
                if (!string.IsNullOrWhiteSpace(metadata.LogoPath) && iconBudget > 0)
                {
                    try
                    {
                        string path = Path.GetFullPath(metadata.LogoPath, version.DirectoryPath);
                        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked instance icon.");
                        var observation = await LoadIconAsync(path, iconBudget, cancellationToken).ConfigureAwait(false);
                        iconBudget -= observation.Bytes; icon = observation.Image;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Instance metadata loaded instance={id} version={version.Id}");
                result.Add(new MinecraftInstanceDescriptor(id, version.DirectoryPath, version.Id, version, metadata)
                { Icon = icon, MetadataError = metadataRead.Error });
            }
            if (_snapshots is not null && catalog.Identity is { } identity)
            {
                // Do not bless a catalog whose inputs changed while metadata/icons were read.
                var after = await Task.Run(() => MinecraftDiscoveryFileIdentity.Capture(root, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (after?.Complete != catalog.CompleteIdentity) throw new IOException("The installed catalog changed during reconciliation.");
                await _snapshots.SaveAsync(root, identity, result, cancellationToken).ConfigureAwait(false);
            }
            operation?.Complete($"count={result.Count}");
            return Array.AsReadOnly(result.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            _log?.Warn(LogModuleName, $"Instance discovery failed current_instance={currentInstance}");
            operation?.Fail(exception);
            throw;
        }
    }

    private async ValueTask<Catalog> GetCatalogAsync(string root, bool refresh, CancellationToken token)
    {
        if (_cache is null)
            return new(await Task.Run(() => _versionDiscovery.Discover(root, token), token).ConfigureAwait(false), null);
        string canonical = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        // Identity observations are flight-only: they are never treated as reusable disk facts.
        var observation = await _cache.GetOrCreateAsync(new StateCacheKey(CatalogScope + ".identity", canonical, "schema-1"),
            new StateCachePolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            async cancellation => new IdentityObservation(await Task.Run(() => MinecraftDiscoveryFileIdentity.Capture(root, cancellation), cancellation).ConfigureAwait(false)),
            shouldStore: static _ => false, cancellationToken: token).ConfigureAwait(false);
        if (observation.Value is not { } diskIdentity)
            return await Task.Run(() =>
            {
                MinecraftDiscoveryFileIdentity.EnsureRootAvailable(root, token);
                var versions = _versionDiscovery.Discover(root, token);
                MinecraftDiscoveryFileIdentity.EnsureRootAvailable(root, token);
                return new Catalog(versions, null);
            }, token).ConfigureAwait(false);
        string identity = diskIdentity.Catalog;
        StateCacheKey key = new(CatalogScope, canonical, identity);
        StateCacheKey authoritativeKey = new(CatalogScope + ".authoritative", canonical, identity);
        StateCachePolicy policy = new(TimeSpan.FromHours(1), TimeSpan.FromHours(1), diskIdentity.EstimatedCatalogBytes);
        if (refresh)
        {
            // Fresh authority cannot join an ordinary producer that may restore display hints.
            Catalog authoritative = await _cache.GetOrCreateAsync(authoritativeKey,
                policy, cancellation => ReadCatalogAsync(root, identity, cancellation), refresh: true,
                cancellationToken: token).ConfigureAwait(false);
            _cache.Invalidate(key);
            return authoritative with { CompleteIdentity = diskIdentity.Complete };
        }
        if (_cache.TryGet<Catalog>(authoritativeKey, out var live))
            return live.Value with { CompleteIdentity = diskIdentity.Complete };
        Catalog catalog = await _cache.GetOrCreateAsync(key, policy, async cancellation =>
        {
            if (_snapshots is not null)
            {
                MinecraftDiscoveryCacheDocument? persisted = await _snapshots.LoadAsync(root, cancellation).ConfigureAwait(false);
                if (persisted?.FileIdentity == identity && persisted.Instances.All(instance =>
                    PathIdentity.Comparer.Equals(instance.Version.JsonPath, MinecraftVersionPaths.FindPrimaryJson(instance.Version.DirectoryPath))))
                    return new Catalog(Array.AsReadOnly(persisted.Instances.Select(instance => instance.Version).ToArray()), identity);
            }
            return await ReadCatalogAsync(root, identity, cancellation).ConfigureAwait(false);
        }, shouldStore: static catalog => catalog.Identity is not null, cancellationToken: token).ConfigureAwait(false);
        return catalog with { CompleteIdentity = diskIdentity.Complete };
    }

    private async ValueTask<Catalog> ReadCatalogAsync(string root, string identity, CancellationToken cancellation)
    {
        IReadOnlyList<MinecraftVersionDescriptor> versions = await Task.Run(
            () => _versionDiscovery.Discover(root, cancellation), cancellation).ConfigureAwait(false);
        var after = await Task.Run(() => MinecraftDiscoveryFileIdentity.Capture(root, cancellation), cancellation).ConfigureAwait(false);
        if (after?.Catalog != identity) throw new IOException("The installed catalog changed during discovery.");
        return new Catalog(Array.AsReadOnly(versions.ToArray()), identity);
    }

    private sealed record IdentityObservation(MinecraftDiscoveryDiskIdentity? Value);
    private sealed record IconObservation(int Bytes, Nexa.Core.Media.PngImage? Image);

    private async ValueTask<IconObservation> LoadIconAsync(string path, int budget, CancellationToken token)
    {
        FileInfo info = new(path);
        if (info.Length > Math.Min(1024 * 1024, budget)) return new(0, null);
        long length = info.Length, modified = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks;
        if (_cache is null) return await ReadAsync(token).ConfigureAwait(false);
        string identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return await _cache.GetOrCreateAsync(new StateCacheKey("minecraft.instance-icon", identity,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"png-1|{length}|{modified}|{created}")),
            new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), length + 4096), ReadAsync,
            shouldStore: static icon => icon.Image is not null, cancellationToken: token).ConfigureAwait(false);

        async ValueTask<IconObservation> ReadAsync(CancellationToken cancellation)
        {
            MinecraftDiscoverySnapshotStore.CheckLinks(path);
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            if (input.Length != length) return new(0, null);
            byte[] bytes = new byte[(int)length]; await input.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            if (input.ReadByte() != -1) return new(bytes.Length, null);
            FileInfo after = new(path);
            if (after.Length != length || after.LastWriteTimeUtc.Ticks != modified || after.CreationTimeUtc.Ticks != created)
                return new(bytes.Length, null);
            return new(bytes.Length, Nexa.Core.Media.PngImage.TryCreate(bytes));
        }
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return PathIdentity.Normalize(root);
    }
}
