using Nexa.Services.Minecraft.Install;

using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceManagementQuery(string InstanceDirectory)
{
    public bool IncludeRecoveryStorage { get; init; }
    public bool IncludeTrash { get; init; }
    public bool CheckModUpdates { get; init; }
    public bool IncludeContentGraph { get; init; }
}
public sealed record InstanceManagementPage(string Id, string Label, string? Directory = null);
public sealed record InstanceContentEntry(string Name, bool IsDirectory, long? Size)
{
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public Nexa.Core.Media.PngImage? Icon { get; init; }
    public bool? Enabled { get; init; }
    public bool? PackageReadable { get; init; }
    public string PackageProblem { get; init; } = "";
    public bool? UpdateAvailable { get; init; }
    public string UpdateVersion { get; init; } = "";
    public long ModifiedUtcTicks { get; init; }
    public InstanceWorldMetadata? World { get; init; }
}
public sealed record InstanceContentSnapshot(string PageId, IReadOnlyList<InstanceContentEntry> Entries, bool Complete, string? Error);
public sealed record InstanceManagementSnapshot(string InstanceDirectory, string GameDirectory, string GameVersion,
    IReadOnlyList<InstallBuildSelection> Components, IReadOnlyList<InstanceManagementPage> Pages,
    bool ModInventoryComplete, string ModpackVersion)
{
    public IReadOnlyList<InstanceContentSnapshot> Contents { get; init; } = [];
    public string Description { get; init; } = "";
    public InstanceRecoveryStorage? RecoveryStorage { get; init; }
    public InstanceRecoveryReport? RecoveryComparison { get; init; }
    public IReadOnlyList<InstanceTrashedContent> Trash { get; init; } = [];
    public InstanceContentGraph? ContentGraph { get; init; }
    public IReadOnlyList<InstanceContentUpdateRecord> ContentUpdates { get; init; } = [];
}

public static class InstanceManagementContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.management.query");
    public static readonly XsrSemanticId SetModEnabled = XsrSemanticId.Parse("minecraft.instance.mod.set-enabled");
    public static readonly XsrSemanticId RemoveContent = XsrSemanticId.Parse("minecraft.instance.content.remove");
    public static readonly XsrSemanticId RestoreContent = XsrSemanticId.Parse("minecraft.instance.content.restore");
    public static readonly XsrSemanticId ModRemovalPreview = XsrSemanticId.Parse("minecraft.instance.mod.removal-preview");
    public static readonly XsrSemanticId RemoveMod = XsrSemanticId.Parse("minecraft.instance.mod.remove");
    public static readonly XsrSemanticId RollbackContentUpdate = XsrSemanticId.Parse("minecraft.instance.content.rollback-update");
}
