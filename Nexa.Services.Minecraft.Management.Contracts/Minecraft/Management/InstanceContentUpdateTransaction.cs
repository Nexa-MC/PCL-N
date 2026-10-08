namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceContentReplacement(InstanceContentRemoveCommand Original, string StagedPath, string NewName);
public sealed record InstanceContentUpdateRecord(Guid Id, DateTimeOffset CreatedAt, int Items, string Phase);
public sealed record InstanceContentUpdateRollbackCommand(string InstanceDirectory, Guid TransactionId);
