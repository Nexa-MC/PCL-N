




namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceContentRemoveCommand(string InstanceDirectory, string PageId, string Name, bool IsDirectory, long? ExpectedSize, long ExpectedModifiedUtcTicks);
public sealed record InstanceContentRestoreCommand(string InstanceDirectory, string TrashId);
public sealed record InstanceTrashedContent(string Id, string PageId, string Name, DateTimeOffset RemovedAt);
