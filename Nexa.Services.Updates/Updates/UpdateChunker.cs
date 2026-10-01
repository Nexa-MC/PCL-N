using System.Buffers;
using System.Security.Cryptography;

namespace Nexa.Services.Updates;

/// <summary>
/// Content-defined chunking over a single sequential scan with rented slab buffers. The hash
/// is computed on the same in-memory slice before the buffer is reused. Blockmap layout
/// identifiers live here because the chunking algorithm and the map format co-signed them.
/// </summary>
public static class UpdateChunker
{
    public const string Algorithm = "pcln-fastcdc-v1";
    public const string AlgorithmV2 = "pcln-fastcdc-v2";
    public const string BlockMapLayoutV1 = "pcln-blockmap-v1";
    public const string SingleFileBlockMapLayoutV1 = "pcln-blockmap-file-v1";
    public const string BlockMapLayoutV2 = "pcln-blockmap-v2";
    public const string SingleFileBlockMapLayoutV2 = "pcln-blockmap-file-v2";

    private const int ReadBufferSize = 256 * 1024;

    private static readonly ulong[] GearTable = BuildGearTable();

    public static Task<IReadOnlyList<UpdateChunkSlice>> ChunkFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        ChunkFileAsync(path, UpdateChunkProfile.V1, cancellationToken);

    public static async Task<IReadOnlyList<UpdateChunkSlice>> ChunkFileAsync(
        string path,
        UpdateChunkProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<UpdateChunkSlice> chunks = [];
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(profile.MaximumSize);
        try
        {
            int chunkLength = 0;
            long chunkOffset = 0;
            ulong rolling = 0;

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await stream.ReadAsync(
                        readBuffer.AsMemory(0, ReadBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int index = 0; index < read; index++)
                {
                    byte value = readBuffer[index];
                    chunkBuffer[chunkLength++] = value;
                    rolling = unchecked((rolling << 1) + GearTable[value]);
                    if (chunkLength < profile.MinimumSize)
                    {
                        continue;
                    }

                    ulong mask = chunkLength < profile.AverageSize ? profile.EarlyMask : profile.LateMask;
                    if ((rolling & mask) != 0 && chunkLength < profile.MaximumSize)
                    {
                        continue;
                    }

                    AddChunk(chunks, chunkBuffer.AsSpan(0, chunkLength), chunkOffset);
                    chunkOffset += chunkLength;
                    chunkLength = 0;
                    rolling = 0;
                }
            }

            if (chunkLength > 0 || chunks.Count == 0)
            {
                AddChunk(chunks, chunkBuffer.AsSpan(0, chunkLength), chunkOffset);
            }

            return chunks;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static void AddChunk(
        List<UpdateChunkSlice> chunks,
        ReadOnlySpan<byte> content,
        long offset)
    {
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        chunks.Add(new UpdateChunkSlice(sha256, offset, content.Length));
    }

    private static ulong[] BuildGearTable()
    {
        ulong[] table = new ulong[256];
        for (int index = 0; index < table.Length; index++)
        {
            table[index] = SplitMix64((ulong)index);
        }

        return table;
    }

    private static ulong SplitMix64(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
