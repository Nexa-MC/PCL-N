





namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceModRemovalQuery(InstanceContentRemoveCommand Primary);
public sealed record InstanceModRemovalPreview(InstanceContentRemoveCommand Primary, IReadOnlyList<InstanceContentRemoveCommand> Orphans, IReadOnlyList<string> RequiredBy, string? Notice);
public sealed record InstanceModRemovalCommand(InstanceContentRemoveCommand Primary, IReadOnlyList<InstanceContentRemoveCommand> Orphans);
