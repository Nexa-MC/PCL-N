using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceWorldMetadata(string LevelName, string GameVersion, int? DataVersion, DateTimeOffset? LastPlayed,
    long? Seed, int? GameMode, int? Difficulty, bool Hardcore, long Size, bool SizeComplete, string Revision,
    bool Locked, IReadOnlyList<InstanceWorldDataPack> DataPacks)
{
    public bool DataPacksSupported { get; init; }
}
public sealed record InstanceWorldDataPack(string Id, string Name, bool Enabled, bool BuiltIn)
{
    public bool Trashed { get; init; }
    public string TrashName { get; init; } = "";
}
public sealed record InstanceWorldMetadataQuery(string InstanceDirectory, string WorldName);
public sealed record InstanceWorldCopyCommand(string InstanceDirectory, string WorldName, string DestinationName, string ExpectedRevision);
public sealed record InstanceWorldBackupCommand(string InstanceDirectory, string WorldName, string ExpectedRevision);
public sealed record InstanceWorldLockCommand(string InstanceDirectory, string WorldName, bool Locked, string ExpectedRevision);
public sealed record InstanceWorldDataPackCommand(string InstanceDirectory, string WorldName, string Id, bool Enabled, string ExpectedRevision);
public sealed record InstanceWorldDataPackImportCommand(string InstanceDirectory, string WorldName, string SourcePath, string ExpectedRevision);
public sealed record InstanceWorldDataPackRemoveCommand(string InstanceDirectory, string WorldName, string Name, string ExpectedRevision);
public sealed record InstanceWorldDataPackRestoreCommand(string InstanceDirectory, string WorldName, string TrashName, string ExpectedRevision);
public static class InstanceWorldContract
{
    public static readonly XsrSemanticId Copy = XsrSemanticId.Parse("minecraft.instance.world.copy");
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.world.read");
    public static readonly XsrSemanticId Backup = XsrSemanticId.Parse("minecraft.instance.world.backup");
    public static readonly XsrSemanticId SetLock = XsrSemanticId.Parse("minecraft.instance.world.set-lock");
    public static readonly XsrSemanticId SetDataPackEnabled = XsrSemanticId.Parse("minecraft.instance.world.datapack.set-enabled");
    public static readonly XsrSemanticId ImportDataPack = XsrSemanticId.Parse("minecraft.instance.world.datapack.import");
    public static readonly XsrSemanticId RemoveDataPack = XsrSemanticId.Parse("minecraft.instance.world.datapack.remove");
    public static readonly XsrSemanticId RestoreDataPack = XsrSemanticId.Parse("minecraft.instance.world.datapack.restore");
}
