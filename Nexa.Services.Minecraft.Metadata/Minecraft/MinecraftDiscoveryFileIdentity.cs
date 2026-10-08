using System.Security.Cryptography;
using System.Text;
using Nexa.Core;

namespace Nexa.Services.Minecraft;

internal sealed record MinecraftDiscoveryDiskIdentity(string Catalog, string Complete, long EstimatedCatalogBytes);

/// <summary>File-set/content identity for catalog reuse; never a security or integrity receipt.</summary>
internal static class MinecraftDiscoveryFileIdentity
{
    private const int MaximumFiles = 32768;
    private const long MaximumJsonBytes = 64L * 1024 * 1024;
    private const int MaximumSingleJsonBytes = 4 * 1024 * 1024;

    internal static MinecraftDiscoveryDiskIdentity? Capture(string root, CancellationToken token)
    {
        EnsureRootAvailable(root, token);
        try
        {
            MinecraftDiscoverySnapshotStore.CheckLinks(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The Minecraft directory is unavailable.");
            using IncrementalHash catalogHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using IncrementalHash completeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string versions = Path.Combine(root, "versions"); MinecraftDiscoverySnapshotStore.CheckLinks(versions);
            AppendBoth(Directory.Exists(versions) ? "versions:present" : "versions:absent");
            if (!Directory.Exists(versions)) return new(Convert.ToHexString(catalogHash.GetHashAndReset()), Convert.ToHexString(completeHash.GetHashAndReset()), 4096);
            string[] directories = Directory.GetDirectories(versions).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (directories.Length > 4096) return null;
            int count = 0; long jsonBytes = 0, catalogJsonBytes = 0; byte[] buffer = new byte[65536];
            foreach (string directory in directories)
            {
                token.ThrowIfCancellationRequested(); MinecraftDiscoverySnapshotStore.CheckLinks(directory);
                AppendBoth(Path.GetRelativePath(root, directory));
                string[] files = Directory.EnumerateFiles(directory).Where(path =>
                    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)).ToArray();
                string[] metadata = [Path.Combine(directory, "Nexa", MinecraftInstanceMetadataStore.MetadataFileName),
                    Path.Combine(directory, "PCL", MinecraftInstanceMetadataStore.MetadataFileName)];
                foreach (string path in files.Concat(metadata).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested(); if (++count > MaximumFiles) return null;
                    bool isMetadata = metadata.Contains(path, PathIdentity.Comparer);
                    MinecraftDiscoverySnapshotStore.CheckLinks(path);
                    Append(completeHash, Path.GetRelativePath(root, path));
                    if (!isMetadata) Append(catalogHash, Path.GetRelativePath(root, path));
                    FileInfo file = new(path);
                    if (!file.Exists) { Append(completeHash, "absent"); if (!isMetadata) Append(catalogHash, "absent"); continue; }
                    long length = file.Length; long stamp = file.LastWriteTimeUtc.Ticks; long created = file.CreationTimeUtc.Ticks;
                    AppendBytes(BitConverter.GetBytes(length)); AppendBytes(BitConverter.GetBytes(stamp)); AppendBytes(BitConverter.GetBytes(created));
                    if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (length > MaximumSingleJsonBytes || jsonBytes + length > MaximumJsonBytes) return null;
                        using FileStream input = new(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                        using IncrementalHash content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long readTotal = 0; int read;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); readTotal += read;
                            if (readTotal > MaximumSingleJsonBytes || jsonBytes + readTotal > MaximumJsonBytes) return null;
                            content.AppendData(buffer.AsSpan(0, read));
                        }
                        if (readTotal != length) return null;
                        jsonBytes += readTotal; if (!isMetadata) catalogJsonBytes += readTotal;
                        AppendBytes(content.GetHashAndReset());
                    }
                    file.Refresh();
                    if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != stamp || file.CreationTimeUtc.Ticks != created) return null;

                    void AppendBytes(byte[] bytes)
                    { completeHash.AppendData(bytes); if (!isMetadata) catalogHash.AppendData(bytes); }
                }
            }
            return new(Convert.ToHexString(catalogHash.GetHashAndReset()), Convert.ToHexString(completeHash.GetHashAndReset()),
                4096L + 8192L * directories.Length + 2 * catalogJsonBytes);

            void AppendBoth(string value) { Append(catalogHash, value); Append(completeHash, value); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { return null; }
    }

    internal static void EnsureRootAvailable(string root, CancellationToken token)
    {
        Probe(root);
        try { Probe(Path.Combine(root, "versions")); }
        catch (FileNotFoundException) { Probe(root); }
        catch (DirectoryNotFoundException) { Probe(root); }

        void Probe(string directory)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                throw new IOException("The Minecraft directory is not a directory.");
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            _ = entries.MoveNext();
            token.ThrowIfCancellationRequested();
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
    }
}
