using System.Security.Cryptography;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceContentIntegrityService
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    public static Task<InstanceContentIntegrity> ReadAsync(InstanceContentIntegrityQuery query, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(query.InstanceDirectory) || query.PageId is not ("mods" or "resourcepacks" or "shaderpacks" or "screenshots" or "schematics")
            || !MinecraftVersionPaths.IsSafeReference(query.Name) || query.ExpectedSize is < 0 or > MaximumFileBytes || query.ExpectedModifiedUtcTicks < 0)
            throw new InvalidDataException("请选择有效的内容文件；完整性读取最多 512 MiB。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory)); var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance))) throw new InvalidDataException("版本必须位于游戏目录的 versions 下。");
        RecoveryBlobStore.CheckLinks(instance);
        using var lease = await InstanceRecoveryOperationGate.EnterOperationAsync(versions.Parent.FullName, token).ConfigureAwait(false);
        var metadataStore = new MinecraftInstanceMetadataStore();
        string metadataPath = metadataStore.GetMetadataPath(instance), legacyPath = Path.Combine(instance, "PCL", MinecraftInstanceMetadataStore.MetadataFileName);
        RecoveryBlobStore.CheckLinks(metadataPath); RecoveryBlobStore.CheckLinks(legacyPath);
        var metadataStamps = new[] { metadataPath, legacyPath }.Select(path =>
        { var file = new FileInfo(path); return (Path: path, Exists: file.Exists, Size: file.Exists ? file.Length : 0, Modified: file.Exists ? file.LastWriteTimeUtc.Ticks : 0); }).ToArray();
        var metadata = await metadataStore.LoadAsync(instance, token).ConfigureAwait(false);
        string game = metadata.InstanceIsolation ? instance : versions.Parent.FullName;
        string path = Path.Combine(game, query.PageId, query.Name); RecoveryBlobStore.CheckLinks(path);
        void CheckIdentity()
        { RecoveryBlobStore.CheckLinks(path); var info = new FileInfo(path); if (!info.Exists || info.Length != query.ExpectedSize || info.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks) throw new IOException("内容已变化，请刷新列表后重新读取完整性。"); }
        CheckIdentity();
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (input.Length != query.ExpectedSize) throw new IOException("内容在读取前变化。");
            byte[] buffer = new byte[81920]; long total = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, query.ExpectedSize - total + 1)), token).ConfigureAwait(false);
                if (read == 0) break; total += read;
                if (total > query.ExpectedSize) throw new IOException("内容在读取期间变化。");
                sha256.AppendData(buffer.AsSpan(0, read)); sha512.AppendData(buffer.AsSpan(0, read));
            }
            if (total != query.ExpectedSize) throw new IOException("内容在读取期间截断。");
        }
        string digest256 = Convert.ToHexString(sha256.GetHashAndReset()), digest512 = Convert.ToHexString(sha512.GetHashAndReset());
        var snapshot = new InstanceManagementSnapshot(instance, game, "", [], [], false, "");
        var baseline = await InstanceContentUpdateTransaction.ReadIntegrityBaselineAsync(snapshot, query, token).ConfigureAwait(false);
        CheckIdentity();
        foreach (var stamp in metadataStamps)
        {
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(stamp.Path); var info = new FileInfo(stamp.Path);
            if (info.Exists != stamp.Exists || info.Exists && (info.Length != stamp.Size || info.LastWriteTimeUtc.Ticks != stamp.Modified)) throw new IOException("实例隔离信息在读取期间变化。");
        }
        return new InstanceContentIntegrity(query, DateTimeOffset.UtcNow, digest256, digest512, baseline.State, baseline.Sha256, baseline.TransactionId,
            baseline.Sha256 is null ? null : !digest256.Equals(baseline.Sha256, StringComparison.OrdinalIgnoreCase));
    }, token);
}
