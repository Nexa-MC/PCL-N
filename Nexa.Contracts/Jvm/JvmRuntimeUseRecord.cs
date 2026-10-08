using System.Buffers.Binary;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Portable bytes shared by Java lifecycle checks and the isolated JVM child bridge.</summary>
internal static class JvmRuntimeUseRecord
{
    internal const int Length = 20;
    internal const string JobsDirectory = ".nexa-java-jobs";
    internal const string UsesDirectory = ".nexa-java-uses";
    internal const string RootLockName = ".nexa-java.lock";

    internal static bool TryRead(ReadOnlySpan<byte> record, out int processId, out long startTimeUtcTicks)
    {
        processId = 0; startTimeUtcTicks = 0;
        if (record.Length != Length || !record[..8].SequenceEqual("NXJAVA01"u8)) return false;
        processId = BinaryPrimitives.ReadInt32LittleEndian(record[8..12]);
        startTimeUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(record[12..]);
        return processId > 0 && startTimeUtcTicks > 0;
    }

    internal static void Write(Span<byte> record, int processId, long startTimeUtcTicks)
    {
        if (record.Length != Length) throw new ArgumentException("Invalid JVM lease record length.", nameof(record));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startTimeUtcTicks);
        "NXJAVA01"u8.CopyTo(record);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..12], processId);
        BinaryPrimitives.WriteInt64LittleEndian(record[12..], startTimeUtcTicks);
    }
}
