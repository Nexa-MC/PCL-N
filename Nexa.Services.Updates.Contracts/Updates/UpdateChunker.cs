


namespace Nexa.Services.Updates;

/// <summary>
/// Content-defined chunking profiles for launcher update block maps. The gear table and cut
/// rule are shared; only size bounds and dual masks differ. Algorithm identifiers, size
/// bounds, and masks are the cross-version data contract and never change.
/// </summary>
public sealed record UpdateChunkProfile(
    string Algorithm,
    int MinimumSize,
    int AverageSize,
    int MaximumSize,
    ulong EarlyMask,
    ulong LateMask)
{
    /// <summary>pcln-fastcdc-v1: 256 KiB / 1 MiB / 2 MiB (masks 21/19).</summary>
    public static UpdateChunkProfile V1 { get; } = new(
        Algorithm: "pcln-fastcdc-v1",
        MinimumSize: 256 * 1024,
        AverageSize: 1024 * 1024,
        MaximumSize: 2 * 1024 * 1024,
        EarlyMask: (1UL << 21) - 1,
        LateMask: (1UL << 19) - 1);

    /// <summary>
    /// pcln-fastcdc-v2: 128 KiB / 512 KiB / 1 MiB (masks 20/18).
    /// Mask spacing matches v1 relative to log2(avg).
    /// </summary>
    public static UpdateChunkProfile V2 { get; } = new(
        Algorithm: "pcln-fastcdc-v2",
        MinimumSize: 128 * 1024,
        AverageSize: 512 * 1024,
        MaximumSize: 1024 * 1024,
        EarlyMask: (1UL << 20) - 1,
        LateMask: (1UL << 18) - 1);

    public static bool TryGet(string? algorithm, out UpdateChunkProfile profile)
    {
        if (string.Equals(algorithm, V1.Algorithm, StringComparison.Ordinal))
        {
            profile = V1;
            return true;
        }

        if (string.Equals(algorithm, V2.Algorithm, StringComparison.Ordinal))
        {
            profile = V2;
            return true;
        }

        profile = V1;
        return false;
    }
}

/// <summary>One content-defined chunk: its raw SHA-256 identity, file offset, and size.</summary>
public sealed record UpdateChunkSlice(string Sha256, long Offset, int Size);
