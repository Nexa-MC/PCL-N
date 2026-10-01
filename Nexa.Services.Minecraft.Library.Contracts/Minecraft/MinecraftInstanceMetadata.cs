



namespace Nexa.Services.Minecraft;

/// <summary>
/// The user-owned per-instance document. Schema 1 intentionally retains the legacy field
/// names and defaults; secrets and runtime state never belong in this file.
/// </summary>
public sealed record MinecraftInstanceMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Description { get; init; } = string.Empty;
    public int LaunchCount { get; init; }
    public string ModpackVersion { get; init; } = string.Empty;
    public string ModpackProjectId { get; init; } = string.Empty;
    public bool IsStarred { get; init; }
    public string LogoPath { get; init; } = string.Empty;
    public int CardType { get; init; }
    public bool DisableAssetVerification { get; init; }
    private readonly string _corePatchSha256 = string.Empty;
    public string CorePatchSha256 { get => _corePatchSha256; init => _corePatchSha256 = value ?? string.Empty; }
    public bool InstanceIsolation { get; init; } = true;
    public string WindowTitle { get; init; } = string.Empty;
    public bool UseGlobalWindowTitle { get; init; } = true;
    public string CustomInfo { get; init; } = string.Empty;
    public int JavaSelectionMode { get; init; }
    public string SelectedJavaPath { get; init; } = string.Empty;
    public int MemorySolution { get; init; } = 2;
    public int CustomMemorySize { get; init; } = 15;
    public int ServerLoginRequirement { get; init; }
    public string AuthServerAddress { get; init; } = string.Empty;
    public string AuthRegisterAddress { get; init; } = string.Empty;
    public string AuthServerDisplayName { get; init; } = string.Empty;
    public bool AuthSettingsLocked { get; init; }
    public string ServerToEnter { get; init; } = string.Empty;
    public int Renderer { get; init; }
    public string JvmArguments { get; init; } = string.Empty;
    public string GameArguments { get; init; } = string.Empty;
    public string ClasspathHead { get; init; } = string.Empty;
    public string WrapperCommand { get; init; } = string.Empty;
    public string PreLaunchCommand { get; init; } = string.Empty;
    public bool WaitForPreLaunchCommand { get; init; } = true;
    public bool IgnoreJavaCompatibility { get; init; }
    public bool UseProxy { get; init; }
    public bool DisableJlw { get; init; }
    public bool DisableRw { get; init; }
    public bool UseDebugLog4j2Config { get; init; }
    public bool DisableLwjglUnsafeAgent { get; init; }
    public bool UseSystemGlfw { get; init; }
    public bool ForceX11OnWayland { get; init; } = true;
}
