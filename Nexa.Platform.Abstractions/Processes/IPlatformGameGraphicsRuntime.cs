namespace Nexa.Platform;

public sealed record PlatformGameGraphicsSnapshot(bool SecondaryAvailable, bool MesaSoftwareAvailable,
    string? SecondaryPciDevice, string Source, string Reason);

public sealed record PlatformGameGraphicsResult(bool Succeeded, string Code, string Message,
    IReadOnlyDictionary<string, string> Environment);

public interface IPlatformGameGraphicsRuntime
{
    PlatformGameGraphicsSnapshot Describe();
    PlatformGameGraphicsResult Prepare(string gpuPreference, string rendererPreference);
}
