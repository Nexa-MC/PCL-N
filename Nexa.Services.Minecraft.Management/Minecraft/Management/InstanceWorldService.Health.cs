using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static partial class InstanceWorldService
{
    private const long HealthBudget = 64L * 1024 * 1024;
    private static readonly uint[] HealthCrcTable = CreateHealthCrcTable();
    private sealed class HealthScan
    {
        internal bool Complete = true;
        internal int Regions, Chunks, DetectedIssues;
        internal long Encoded, Decoded;
        internal List<string> Issues { get; } = [];
        internal void Issue(string message, bool incomplete = false)
        { DetectedIssues++; if (Issues.Count < 100) Issues.Add(message); Complete &= !incomplete; }
        internal InstanceWorldHealth Result() => new(Complete, Regions, Chunks, DetectedIssues, Issues.AsReadOnly());
    }

    public static async Task<InstanceWorldHealth> ReadHealthAsync(InstanceWorldHealthQuery query, XsrStateStore store,
        Func<string, IDisposable>? acquireSessionLock = null, CancellationToken token = default)
    {
        if (!MinecraftVersionPaths.IsSafeReference(query.WorldName) || query.WorldName.StartsWith(".nexa-", StringComparison.Ordinal)) throw new InvalidDataException("世界名称无效。");
        var snapshot = await InstanceManagementService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        string world = Path.Combine(snapshot.GameDirectory, "saves", query.WorldName); RecoveryBlobStore.CheckLinks(world);
        var scan = new HealthScan();
        try
        {
            using var gate = await InstanceRecoveryOperationGate.EnterOperationAsync(Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            InstanceContentTrash.RejectRunning(snapshot, store, token);
            using var session = AcquireSessionLock(world, acquireSessionLock);
            var (_, revision) = await ReadLevelAsync(world, token: token).ConfigureAwait(false);
            var regions = HealthRegionFiles(world, scan, token);
            var sourceStamps = regions.ToDictionary(path => path, path => { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc.Ticks); }, Nexa.Core.PathIdentity.Comparer);
            foreach (string path in regions)
            {
                token.ThrowIfCancellationRequested();
                try { await ScanRegionAsync(path, scan, token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
                { scan.Issue(Path.GetRelativePath(world, path) + ": " + error.Message, true); }
            }
            var finalRegions = HealthRegionFiles(world, scan, token);
            if (finalRegions.Count != regions.Count || finalRegions.Any(path => !sourceStamps.ContainsKey(path))) scan.Issue("检查期间 Region 文件集合变化。", true);
            foreach (var original in sourceStamps)
            {
                RecoveryBlobStore.CheckLinks(original.Key); var file = new FileInfo(original.Key);
                if (!file.Exists || file.Length != original.Value.Length || file.LastWriteTimeUtc.Ticks != original.Value.Ticks) scan.Issue("检查期间 Region 文件变化。", true);
            }
            if ((await ReadLevelAsync(world, token: token).ConfigureAwait(false)).Revision != revision) scan.Issue("检查期间世界元数据变化。", true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException)
        { scan.Issue(error.Message, true); }
        return scan.Result();
    }

    private static List<string> HealthRegionFiles(string world, HealthScan scan, CancellationToken token)
    {
        List<string> regions = []; Stack<(string Path, int Depth)> pending = new(); pending.Push((world, 0)); int entries = 0;
        while (pending.TryPop(out var directory))
        {
            if (directory.Depth > 32) { scan.Issue("世界目录层级超过健康检查预算。", true); continue; }
            foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 10000) { scan.Issue("世界文件数量超过健康检查预算。", true); return regions; }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { scan.Issue("世界包含链接，未读取链接内容。", true); continue; }
                if (entry is DirectoryInfo)
                { if (!entry.Name.StartsWith(".nexa-", StringComparison.Ordinal)) pending.Push((entry.FullName, directory.Depth + 1)); }
                else if (entry.Extension.Equals(".mca", StringComparison.OrdinalIgnoreCase) && new DirectoryInfo(directory.Path).Name is "region" or "entities" or "poi")
                {
                    if (regions.Count == 256) { scan.Issue("Region 文件数量超过 256 个检查预算。", true); return regions; }
                    regions.Add(entry.FullName);
                }
            }
        }
        return regions;
    }

    private static async Task ScanRegionAsync(string path, HealthScan scan, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path); var stamp = new FileInfo(path); long bytes = stamp.Length, modified = stamp.LastWriteTimeUtc.Ticks;
        if (bytes < 8192 || bytes % 4096 != 0) throw new InvalidDataException("Region 文件头或末尾扇区已截断。");
        if (bytes > 1024L * 1024 * 1024) { scan.Issue("Region 文件超过 1 GiB 检查预算。", true); return; }
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        byte[] header = new byte[8192]; await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        bool[] used = new bool[(int)(bytes / 4096)]; used[0] = used[1] = true;
        List<(int Slot, int Offset, int Sectors)> allocations = [];
        for (int slot = 0; slot < 1024; slot++)
        {
            int start = slot * 4, offset = header[start] << 16 | header[start + 1] << 8 | header[start + 2], sectors = header[start + 3];
            if (offset == 0 && sectors == 0) continue;
            if (offset < 2 || sectors == 0 || (long)offset + sectors > used.Length)
            { scan.Issue(Path.GetFileName(path) + ": 区块扇区分配越界。", true); continue; }
            bool overlap = false;
            for (int sector = offset; sector < offset + sectors; sector++) { overlap |= used[sector]; used[sector] = true; }
            if (overlap) { scan.Issue(Path.GetFileName(path) + ": 区块扇区分配重叠。", true); continue; }
            allocations.Add((slot, offset, sectors));
        }
        scan.Regions++;
        byte[] prefix = new byte[5];
        foreach (var allocation in allocations)
        {
            token.ThrowIfCancellationRequested();
            if (scan.Chunks == 4096 || scan.Encoded >= HealthBudget || scan.Decoded >= HealthBudget)
            { scan.Issue("区块数量或压缩/解压总量超过健康检查预算。", true); return; }
            input.Position = (long)allocation.Offset * 4096; await input.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix); byte encoding = (byte)(prefix[4] & 127); bool external = (prefix[4] & 128) != 0;
            if (length < 1 || length > (long)allocation.Sectors * 4096 - 4) { scan.Issue(Path.GetFileName(path) + ": 区块长度超出分配扇区。", true); continue; }
            if (encoding is not (1 or 2 or 3)) { scan.Issue(Path.GetFileName(path) + ": 不支持此区块压缩编码，检查不完整。", true); continue; }
            byte[] encoded;
            if (external)
            {
                if (length != 1) { scan.Issue(Path.GetFileName(path) + ": 外部区块头长度无效。", true); continue; }
                string externalPath = ExternalChunkPath(path, allocation.Slot); RecoveryBlobStore.CheckLinks(externalPath);
                var externalStamp = new FileInfo(externalPath);
                if (!externalStamp.Exists || externalStamp.Length is <= 0 or > Limit) { scan.Issue(Path.GetFileName(path) + ": 外部区块不存在或超过预算。", true); continue; }
                long externalBytes = externalStamp.Length, externalModified = externalStamp.LastWriteTimeUtc.Ticks;
                await using var externalInput = new FileStream(externalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                using var externalBuffer = new MemoryStream(); await CopyBoundedAsync(externalInput, externalBuffer, Limit, token).ConfigureAwait(false); encoded = externalBuffer.ToArray();
                externalStamp.Refresh();
                if (!externalStamp.Exists || encoded.LongLength != externalBytes || externalStamp.Length != externalBytes || externalStamp.LastWriteTimeUtc.Ticks != externalModified)
                    throw new IOException("外部区块读取期间变化。");
            }
            else
            {
                if (length - 1 > Limit) { scan.Issue("单个区块超过 8 MiB 压缩预算。", true); continue; }
                encoded = new byte[(int)length - 1]; await input.ReadExactlyAsync(encoded, token).ConfigureAwait(false);
            }
            if (encoded.LongLength > HealthBudget - scan.Encoded) { scan.Issue("区块压缩总量超过检查预算。", true); return; }
            scan.Encoded += encoded.LongLength;
            using var source = new MemoryStream(encoded, false);
            using Stream decoded = encoding switch { 1 => new GZipStream(source, CompressionMode.Decompress), 2 => new ZLibStream(source, CompressionMode.Decompress), _ => source };
            using var raw = new MemoryStream();
            await CopyBoundedAsync(decoded, raw, Math.Min(Limit, HealthBudget - scan.Decoded), token).ConfigureAwait(false);
            scan.Decoded += raw.Length; byte[] nbt = raw.ToArray(); ValidateChunkChecksum(encoded, nbt, encoding); _ = ServerNbt.Parse(nbt); scan.Chunks++;
        }
        stamp.Refresh();
        if (!stamp.Exists || stamp.Length != bytes || stamp.LastWriteTimeUtc.Ticks != modified) throw new IOException("Region 文件检查期间变化。");
    }

    private static string ExternalChunkPath(string region, int slot)
    {
        string[] name = Path.GetFileNameWithoutExtension(region).Split('.');
        if (name.Length != 3 || name[0] != "r" || !int.TryParse(name[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(name[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)) throw new InvalidDataException("Region 文件名坐标无效。");
        long chunkX = (long)x * 32 + slot % 32, chunkZ = (long)z * 32 + slot / 32;
        return Path.Combine(Path.GetDirectoryName(region)!, $"c.{chunkX.ToString(CultureInfo.InvariantCulture)}.{chunkZ.ToString(CultureInfo.InvariantCulture)}.mcc");
    }

    private static uint[] CreateHealthCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        { uint value = index; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1; table[index] = value; }
        return table;
    }
    private static void ValidateChunkChecksum(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> raw, byte encoding)
    {
        if (encoding == 1)
        {
            if (encoded.Length < 18 || encoded[0] != 31 || encoded[1] != 139 || encoded[2] != 8 || (encoded[3] & 224) != 0)
                throw new InvalidDataException("GZip 区块头已截断或无效。");
            uint crc = uint.MaxValue; foreach (byte value in raw) crc = HealthCrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[^8..]) != ~crc || BinaryPrimitives.ReadUInt32LittleEndian(encoded[^4..]) != raw.Length)
                throw new InvalidDataException("GZip 区块校验或尾部长度无效。");
        }
        else if (encoding == 2)
        {
            if (encoded.Length < 6 || (encoded[0] & 15) != 8 || (encoded[0] >> 4) > 7 || ((encoded[0] << 8) | encoded[1]) % 31 != 0 || (encoded[1] & 32) != 0)
                throw new InvalidDataException("ZLib 区块头已截断或需要未支持的字典。");
            uint a = 1, b = 0;
            for (int start = 0; start < raw.Length; start += 5552)
            { foreach (byte value in raw.Slice(start, Math.Min(5552, raw.Length - start))) { a += value; b += a; } a %= 65521; b %= 65521; }
            if (BinaryPrimitives.ReadUInt32BigEndian(encoded[^4..]) != (b << 16 | a)) throw new InvalidDataException("ZLib 区块校验或尾部已截断。");
        }
    }
}
