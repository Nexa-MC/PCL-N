




using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public enum InstanceRecoveryChangeKind { Added, Removed, Modified, Enabled, Disabled }
public sealed record InstanceRecoveryQuery(string InstanceDirectory);
public sealed record InstanceRecoveryChange(InstanceRecoveryChangeKind Kind, string Category, string Path, string? SettingKey = null)
{
    public string? Area { get; init; }
    public string? RelatedPath { get; init; }
}
public sealed record InstanceRecoveryReport(string InstanceDirectory, Guid? BaselineRevision, DateTimeOffset? CapturedAt,
    IReadOnlyList<InstanceRecoveryChange> Changes, string Fingerprint, string? UnavailableReason = null);
public static class InstanceRecoveryContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.recovery.query");
    public static readonly XsrSemanticId Restore = XsrSemanticId.Parse("minecraft.instance.recovery.restore");
    public static readonly XsrSemanticId Recover = XsrSemanticId.Parse("minecraft.instance.recovery.recover");
}
