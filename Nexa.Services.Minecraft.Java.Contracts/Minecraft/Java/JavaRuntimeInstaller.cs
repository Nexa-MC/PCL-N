



namespace Nexa.Services.Minecraft.Java;

public sealed record JavaRuntimeInstallProgress(
    string Stage,
    double Progress,
    int CompletedFiles,
    int TotalFiles,
    string? Detail = null);

/// <summary>Acquisition seam used by launch orchestration and deterministic tests.</summary>
public interface IJavaRuntimeInstaller
{
    Task<string> InstallAsync(
        string requestedComponent,
        string runtimeRootDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Revalidates a reviewed plan before acquiring or publishing any runtime files.</summary>
public interface IConfirmedJavaRuntimeInstaller : IJavaRuntimeInstaller
{
    Task<string> InstallConfirmedAsync(string component, string runtimeRootDirectory, string expectedPlanFingerprint,
        IProgress<JavaRuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default);
}
