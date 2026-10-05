using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Process;

public enum MinecraftProcessOutputChannel { Stdout, Stderr }

/// <summary>One bounded, redacted pipe-output entry, ordered within its process session.</summary>
public sealed record MinecraftProcessOutputEntry(long Sequence, MinecraftProcessOutputChannel Stream, string Text);

public sealed record MinecraftProcessOutputQuery(Guid SessionId);

/// <summary>A detached tail; DroppedEntries counts entries older than the retained window.</summary>
public sealed record MinecraftProcessOutputSnapshot(Guid SessionId, string InstanceId, MinecraftProcessState State,
    long Revision, long DroppedEntries, IReadOnlyList<MinecraftProcessOutputEntry> Entries);

public static class MinecraftProcessOutputContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.process.output.read");
}
