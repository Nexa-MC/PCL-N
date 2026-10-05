namespace Nexa.Platform;

public enum PlatformProcessIdentityState
{
    Unknown,
    Running,
    Exited
}

/// <summary>Exited proves absence or termination; unknown never grants permission to delete.</summary>
public readonly record struct PlatformProcessIdentityObservation(
    PlatformProcessIdentityState State, long? StartTimeUtcTicks = null);

public interface IPlatformProcessIdentity
{
    PlatformProcessIdentityObservation Observe(int processId);
}
