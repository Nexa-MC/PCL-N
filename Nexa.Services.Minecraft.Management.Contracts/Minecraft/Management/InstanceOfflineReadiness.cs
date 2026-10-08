using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceOfflineReadinessQuery(string InstanceDirectory);
public enum OfflineArtifactState { Verified, PresentUnverified, Missing, Corrupt, Unavailable }
public sealed record OfflineArtifactFact(string Category, string RelativePath, OfflineArtifactState State, long? Bytes, string ReasonCode);
public sealed record InstanceOfflineReadinessReport(string InstanceDirectory, DateTimeOffset CapturedAt,
    bool Complete, bool? JavaCompatible, string? JavaVersion, IReadOnlyList<OfflineArtifactFact> Artifacts)
{
    public bool Ready => Complete && JavaCompatible == true && Artifacts.Count > 0 && Artifacts.All(x => x.State == OfflineArtifactState.Verified);
}
public static class InstanceOfflineReadinessContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.offline-readiness");
}
