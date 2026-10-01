



namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceModEnabledCommand(string InstanceDirectory, string Name, bool Enabled, long ExpectedSize, long ExpectedModifiedUtcTicks);
