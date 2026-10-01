

namespace Nexa.Services.Capabilities;

public sealed record ResourceObservationSample(
    string InstanceKey,
    string Loader,
    int JavaMajor,
    long ModCount,
    long ResourceWeight,
    long HeapPeakMiB,
    long NativePeakMiB,
    long PhysicalPeakMiB,
    long CommitPeakMiB,
    long GpuPeakMiB,
    long LaunchDurationMilliseconds,
    DateTimeOffset Timestamp)
{
    public string? ModFingerprint { get; init; }
    public string? SettingsFingerprint { get; init; }
}

internal static class ResourceHistoryCatalog
{
    public static readonly CapabilityDefinition<string> SettingsFingerprint = new(
        "minecraft.settings.fingerprint", "游戏设置指纹", "游戏设置", MachineInstanceCatalog.MinecraftProviderId,
        CapabilityKind.Fact, CapabilityStability.Dynamic);
}
