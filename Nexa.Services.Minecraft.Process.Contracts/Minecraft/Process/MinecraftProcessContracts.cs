







namespace Nexa.Services.Minecraft.Process;

public enum MinecraftProcessState
{
    Created,
    Running,
    Exited,
    Failed,
    Cancelled,
}

public sealed record MinecraftProcessSnapshot(
    Guid SessionId,
    string InstanceId,
    int ProcessId,
    MinecraftProcessState State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    public string InstanceDirectory { get; init; } = string.Empty;
    public string GameDirectory { get; init; } = string.Empty;
    public bool GameWindowConfirmed { get; init; }
}
