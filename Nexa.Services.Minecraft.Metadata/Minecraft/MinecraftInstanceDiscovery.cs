using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft;

/// <summary>
/// Discovers installed version/instance directories without loading UI state. Metadata is read
/// through the same atomic store used by instance commands, so discovery never observes a
/// partially written document.
/// </summary>
public sealed class MinecraftInstanceDiscovery(
    LogService? log = null,
    MinecraftVersionDiscovery? versionDiscovery = null,
    MinecraftInstanceMetadataStore? metadataStore = null) : IMinecraftInstanceSource
{
    private const string LogModuleName = "InstanceScan";

    private readonly LogService? _log = log;
    private readonly MinecraftVersionDiscovery _versionDiscovery = versionDiscovery ?? new MinecraftVersionDiscovery();
    private readonly MinecraftInstanceMetadataStore _metadataStore = metadataStore ?? new MinecraftInstanceMetadataStore();

    public async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(
        string minecraftRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        using LogOperation? operation = _log?.BeginOperation(LogModuleName, "DiscoverInstances", $"root={minecraftRootDirectory}");
        string? currentInstance = null;
        try
        {
            operation?.Stage("discover_versions");
            IReadOnlyList<MinecraftVersionDescriptor> versions = await Task.Run(
                () => _versionDiscovery.Discover(minecraftRootDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
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
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
                        if (input.Length <= Math.Min(1024 * 1024, iconBudget))
                        {
                            byte[] bytes = new byte[(int)input.Length];
                            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                            iconBudget -= bytes.Length; icon = Nexa.Core.Media.PngImage.TryCreate(bytes);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                _log?.Write(LogLevel.RealTime, LogModuleName,
                    $"Instance metadata loaded instance={id} version={version.Id}");
                result.Add(new MinecraftInstanceDescriptor(id, version.DirectoryPath, version.Id, version, metadata)
                { Icon = icon, MetadataError = metadataRead.Error });
            }
            operation?.Complete($"count={result.Count}");
            return result;
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
}
