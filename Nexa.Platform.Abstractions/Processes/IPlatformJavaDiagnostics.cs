namespace Nexa.Platform;

public enum PlatformJavaProbeKind { Properties, Modules }
public enum PlatformJavaProbeStatus { Available, Rejected, IdentityChanged, TimedOut, OutputLimit, Failed }
public sealed record PlatformJavaExecutableIdentity(string Executable, long Size, long ModifiedUtcTicks, string Sha256);
public sealed record PlatformJavaProbeOutput(PlatformJavaProbeStatus Status, int? ExitCode, string StandardOutput, string StandardError);

/// <summary>Fixed read-only Java diagnostics. Product inventory owns executable admission.</summary>
public interface IPlatformJavaDiagnostics
{
    ValueTask<PlatformJavaExecutableIdentity> CaptureIdentityAsync(string executable, CancellationToken cancellationToken = default);
    ValueTask<PlatformJavaProbeOutput> ProbeAsync(PlatformJavaExecutableIdentity identity, PlatformJavaProbeKind kind,
        CancellationToken cancellationToken = default);
}
