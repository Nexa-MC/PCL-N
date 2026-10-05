using Nexa.Xsr;

namespace Nexa.Services.Setup;

public sealed record StoragePreferencesStatus(string DataDirectory, bool LocationLocked, bool MigrationPending)
{
    public string? PendingTransaction { get; init; }
}
public sealed record StoragePreferencesQuery;
public sealed record StorageMigrationQuery(string DestinationDirectory);
public sealed record StorageMigrationPreview(string SourceDirectory, string DestinationDirectory, string Revision, int Files, long Bytes);
public sealed record StorageMigrationCommand(string DestinationDirectory, string ExpectedRevision);
public sealed record StorageMigrationCancelCommand(string ExpectedTransaction);
public enum StorageCleanupKind { TemporaryFiles, FinishedTasks }
public sealed record StorageCleanupQuery(StorageCleanupKind Kind);
public sealed record StorageCleanupEntry(string Identity, long Bytes);
public sealed record StorageCleanupPreview(StorageCleanupKind Kind, string Revision, IReadOnlyList<StorageCleanupEntry> Entries);
public sealed record StorageCleanupCommand(StorageCleanupKind Kind, string ExpectedRevision);
public static class StoragePreferencesContract
{
    public static readonly XsrSemanticId Status = XsrSemanticId.Parse("setup.storage.status");
    public static readonly XsrSemanticId MigrationPreview = XsrSemanticId.Parse("setup.storage.migrate.preview");
    public static readonly XsrSemanticId Migrate = XsrSemanticId.Parse("setup.storage.migrate");
    public static readonly XsrSemanticId CancelMigration = XsrSemanticId.Parse("setup.storage.migrate.cancel");
    public static readonly XsrSemanticId CleanupPreview = XsrSemanticId.Parse("setup.storage.cleanup.preview");
    public static readonly XsrSemanticId Cleanup = XsrSemanticId.Parse("setup.storage.cleanup");
}
