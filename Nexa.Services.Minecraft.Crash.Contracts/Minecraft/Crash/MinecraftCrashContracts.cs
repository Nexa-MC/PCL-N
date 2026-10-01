

namespace Nexa.Services.Minecraft.Crash;

public enum MinecraftLaunchFaultCode
{
    Unknown,
    JavaRuntimeMissing,
    JavaRuntimeIncompatible,
    JvmInitializationFailed,
    MainClassMissing,
    ClasspathDependencyMissing,
    AuthenticationFailed,
    SessionServiceUnavailable,
    NativeLibraryFailed,
    GraphicsInitializationFailed,
    ModLoaderBootstrapFailed,
    ModConflict,
    MissingModDependency,
    OutOfMemory,
    FileLocked,
    AccessDenied,
}

public enum MinecraftRepairActionKind
{
    InspectOnly,
    RepairVersionFiles,
    ReextractNatives,
    InstallMissingModDependencies,
    DownloadMod,
    DisableMod,
    UpdateMod,
    ReadModMetadata,
    SelectCompatibleJava,
    DownloadCompatibleJava,
    ReinstallVersionAndUpdateLoader,
    RefreshAccount,
    ReduceMemoryPressure,
    ReviewModSet,
    DisableExperimentalJvmHost,
}

public sealed record MinecraftLaunchFaultReport
{
    public MinecraftLaunchFaultCode Code { get; init; }
    public string Stage { get; init; } = "Unknown";
    public string Subsystem { get; init; } = "Minecraft";
    public string ExceptionType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    public string? LastClassName { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<MinecraftRepairActionKind> AllowedActions { get; init; } = [MinecraftRepairActionKind.InspectOnly];
}

public sealed record MinecraftMissingDependency(string Name, string ModId, string? RequiredVersion);
