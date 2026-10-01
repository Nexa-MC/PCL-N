






namespace Nexa.Services.Minecraft.Process;

public sealed record LaunchModIdentity(string Id, string Version, string Format, bool Enabled,
    IReadOnlyDictionary<string, string> Dependencies, bool DependenciesComplete)
{
    public string? ContentSha256 { get; init; }
    public bool NestedCandidate { get; init; }
    public IReadOnlyDictionary<string, string> ProvidedIds { get; init; } = new Dictionary<string, string>();
}
public sealed record LaunchModInventory(IReadOnlyList<LaunchModIdentity> Mods, int UnknownFiles, bool Complete);
public sealed record JvmRunContext(Guid SessionId, string Loader, string LoaderVersion, LaunchModInventory Inventory)
{
    public string GameVersion { get; init; } = "unknown";
    public IReadOnlyDictionary<string, string> Components { get; init; } = new Dictionary<string, string>();
}
