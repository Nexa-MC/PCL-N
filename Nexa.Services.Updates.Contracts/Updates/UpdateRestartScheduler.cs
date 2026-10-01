

namespace Nexa.Services.Updates;

/// <summary>
/// One downloaded-and-verified launcher update, ready to hand off to the replacement
/// process. The staged executable is complete and GPG-verified; the optional install plan
/// file makes the replacement run in tree-update mode.
/// </summary>
public sealed record PreparedLauncherUpdate(
    UpdatePackage Package,
    string CurrentExecutablePath,
    string StagedExecutablePath,
    string WorkDirectory,
    bool UsedPatch,
    bool UsedBlockMap = false)
{
    /// <summary>
    /// The install plan file for tree updates (scatter/block payloads); null for plain
    /// single-binary updates.
    /// </summary>
    public string? InstallPlanPath { get; init; }
}
