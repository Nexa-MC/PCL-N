using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceServerEntry(int SourceIndex, string Name, string Address, string? Icon = null, bool? AcceptTextures = null);
public sealed record InstanceServerList(string Revision, IReadOnlyList<InstanceServerEntry> Entries);
public sealed record InstanceServerListQuery(string InstanceDirectory);
public sealed record InstanceServerListSaveCommand(string InstanceDirectory, string ExpectedRevision, IReadOnlyList<InstanceServerEntry> Entries);
public sealed record InstanceServerStatusQuery(string InstanceDirectory, string ExpectedRevision, int SourceIndex);
public sealed record InstanceServerStatus(bool Reachable, string Description, string Version, int? OnlinePlayers, int? MaxPlayers, long Milliseconds);
public static class InstanceServerListContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.servers.read");
    public static readonly XsrSemanticId Save = XsrSemanticId.Parse("minecraft.instance.servers.save");
    public static readonly XsrSemanticId Status = XsrSemanticId.Parse("minecraft.instance.servers.status");
}
