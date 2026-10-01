

namespace Nexa.Services.Minecraft.Process;

public enum MinecraftWindowProbeResult
{
    /// <summary>This platform has no window detection wired; the caller skips waiting.</summary>
    Unsupported,

    /// <summary>Detection is supported and the process owns no visible window yet.</summary>
    NotVisible,

    /// <summary>The process owns a visible top-level window.</summary>
    Visible,
}

/// <summary>
/// Detects whether the launched game process owns a visible top-level window. The launch
/// pipeline uses this as the legacy "wait for window" confirmation: the narration stays honest
/// until the game has actually presented itself. Unsupported must be distinguishable from
/// not-visible, or platforms without a probe would stall the launch for the whole wait limit.
/// </summary>
public interface IMinecraftWindowProbe
{
    ValueTask<MinecraftWindowProbeResult> ProbeAsync(int processId, CancellationToken cancellationToken = default);
}
