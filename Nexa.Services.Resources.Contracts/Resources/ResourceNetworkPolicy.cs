namespace Nexa.Services.Resources;

public sealed record ResourceNetworkPolicyQuery;
public sealed record ResourceSourceResolution(ResourceProvider Provider, DateTimeOffset ObservedAt, string Priority,
    bool RequestedMirrorFirst, bool EffectiveMirrorFirst, IReadOnlyList<string> CandidateHosts, string? SelectedHost);
public sealed record ResourceNetworkPolicySnapshot(string Priority, bool AutoInstallDependencies, ResourceSourceResolution? LastResolution);

public static class ResourceSourcePriority
{
    public static bool Apply(string? priority, bool requestedMirrorFirst) => priority switch
    { "official-first" => false, "mirrors-first" => true, _ => requestedMirrorFirst };
}
