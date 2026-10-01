using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>
/// One launch pipeline report: the stage token, overall progress, whether the game is
/// running, the login method label, and the optional download speed line. This is the data
/// contract behind the launch progress overlay.
/// </summary>
public readonly record struct MinecraftLaunchStageReport(
    string Stage,
    double Progress,
    bool IsLaunched = false,
    string? Method = null,
    string? DownloadSpeed = null,
    Guid? SessionId = null);

/// <summary>
/// One atomic launch-progress truth. Scalar state keys are derived compatibility projections of
/// this value, so observers can never combine fields from different reports.
/// </summary>
public sealed record MinecraftLaunchProgressSnapshot(
    bool Active,
    string Stage,
    double Progress,
    string Method,
    string DownloadSpeed,
    bool IsLaunched,
    Guid? SessionId)
{
    public string? InstanceId { get; init; }
    public string? MinecraftRootDirectory { get; init; }

    public static MinecraftLaunchProgressSnapshot Empty { get; } =
        new(false, string.Empty, 0d, string.Empty, string.Empty, false, null);
}

/// <summary>
/// The legacy launch stage weights, migrated unchanged: every pipeline step owns a share of
/// the total launch effort, and the reported progress is the completed weight over that
/// total. Stage tokens are stable identifiers; display strings stay on the desktop side.
/// </summary>
public static class MinecraftLaunchStages
{
    public const string GetJava = "get_java";
    public const string Login = "login";
    public const string CompleteFiles = "complete_files";
    public const string GetArguments = "get_arguments";
    public const string ExtractNatives = "extract_natives";
    public const string PreLaunch = "pre_launch";
    public const string StartProcess = "start_process";
    public const string WaitWindow = "wait_window";
    public const string End = "end";

    public const double GetJavaWeight = 4d;
    public const double LoginWeight = 15d;
    public const double CompleteFilesWeight = 15d;
    public const double GetArgumentsWeight = 2d;
    public const double ExtractNativesWeight = 2d;
    public const double PreLaunchWeight = 1d;
    public const double StartProcessWeight = 2d;
    public const double WaitWindowWeight = 1d;
    public const double EndWeight = 1d;

    // The legacy table carries one weight for custom_command, whose feature has not migrated;
    // it stays reserved so every migrated stage reports the same overall pacing as the legacy
    // launch. Migrated stages (including wait_window and pre_launch) are consumed for real.
    public const double CustomCommandWeight = 1d;
    public const double Total = 44d;

    public static double ProgressAt(double completedWeight) =>
        Math.Clamp(completedWeight / Total, 0d, 1d);
}

/// <summary>
/// The launch progress state cells. The coordinator publishes one report as one coherent set
/// of cells; the renderer reads local state and the desktop controller owns display strings.
/// </summary>
public static class MinecraftLaunchProgressState
{
    public const string OwnerName = "Nexa.Services.Minecraft.Launch";

    public static readonly XsrSemanticId SnapshotKey = XsrSemanticId.Parse("minecraft.launch.snapshot");
    public static readonly XsrSemanticId ActiveKey = XsrSemanticId.Parse("minecraft.launch.active");
    public static readonly XsrSemanticId StageKey = XsrSemanticId.Parse("minecraft.launch.stage");
    public static readonly XsrSemanticId ProgressKey = XsrSemanticId.Parse("minecraft.launch.progress");
    public static readonly XsrSemanticId MethodKey = XsrSemanticId.Parse("minecraft.launch.method");
    public static readonly XsrSemanticId SpeedKey = XsrSemanticId.Parse("minecraft.launch.speed");
    public static readonly XsrSemanticId LaunchedKey = XsrSemanticId.Parse("minecraft.launch.launched");

    // Java runtime acquisition approval: the pipeline pauses before any download until the
    // user decides (the legacy launcher asks before auto-downloading a runtime).
    public static readonly XsrSemanticId AcquirePendingKey = XsrSemanticId.Parse("minecraft.java.acquire.pending");
    public static readonly XsrSemanticId AcquireComponentKey = XsrSemanticId.Parse("minecraft.java.acquire.component");
    public static readonly XsrSemanticId JavaChoicesKey = XsrSemanticId.Parse("minecraft.java.choice.versions");
    public static readonly XsrSemanticId AcquireMajorKey = XsrSemanticId.Parse("minecraft.java.acquire.major");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        LaunchPreflightContract.DeclareState(builder);
        builder.Cell<MinecraftLaunchProgressSnapshot>(SnapshotKey, OwnerName);
        builder.Derived(ActiveKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Active);
        builder.Derived(StageKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Stage);
        builder.Derived(ProgressKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Progress);
        builder.Derived(MethodKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).Method);
        builder.Derived(SpeedKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).DownloadSpeed);
        builder.Derived(LaunchedKey, OwnerName, [SnapshotKey],
            static (reader, cancellationToken) => ReadSnapshot(reader, cancellationToken).IsLaunched);
        builder.Cell<bool>(AcquirePendingKey, OwnerName);
        builder.Cell<string>(AcquireComponentKey, OwnerName);
        builder.Cell<int>(AcquireMajorKey, OwnerName);
        builder.Cell<IReadOnlyList<int>>(JavaChoicesKey, OwnerName);
    }

    private static MinecraftLaunchProgressSnapshot ReadSnapshot(
        XsrStateReader reader,
        CancellationToken cancellationToken)
    {
        XsrStateValue<MinecraftLaunchProgressSnapshot> value = reader.Read<MinecraftLaunchProgressSnapshot>(
            reader.Resolve(SnapshotKey),
            cancellationToken);
        return value.HasValue ? value.Value : MinecraftLaunchProgressSnapshot.Empty;
    }
}
