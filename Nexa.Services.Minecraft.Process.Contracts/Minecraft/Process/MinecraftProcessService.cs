using Nexa.Services.Minecraft.Crash;



namespace Nexa.Services.Minecraft.Process;

/// <summary>
/// Composition-phase state declaration for the Minecraft process capability.
/// </summary>
public sealed record MinecraftProcessFailure(Guid SessionId, string InstanceId, MinecraftLaunchFaultReport Report)
{
    public string? InstanceDirectory { get; init; }
}
