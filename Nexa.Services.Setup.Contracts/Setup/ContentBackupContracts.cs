namespace Nexa.Services.Setup;

public sealed record ContentBackupFile(string RelativePath, string Sha256, long Bytes);
public sealed record ContentBackupManifest(string Identity, string Label, DateTimeOffset CreatedAt, IReadOnlyList<ContentBackupFile> Files);
public sealed record ContentStoreStatistics(int Backups, int Objects, long PhysicalBytes, long LogicalBytes, long ReferencedBytes, long ReclaimableBytes);
public sealed record ContentPrunePreview(string Revision, IReadOnlyList<string> BackupIdentities, int Objects, long Bytes);
public sealed record OfflineReadinessReport(string BackupIdentity, int Files, int VerifiedFiles, IReadOnlyList<string> MissingOrCorruptPaths)
{
    public bool Ready => Files == VerifiedFiles && MissingOrCorruptPaths.Count == 0;
}
public sealed record LegacyMigrationPreview(string SourceDirectory, string DestinationDirectory, string Revision,
    int Settings, int OfflineAccounts, int ReauthenticationAccounts, IReadOnlyList<string> Warnings);

public sealed record ContentBackupListQuery;
public sealed record ContentStoreStatisticsQuery;
public sealed record ContentBackupCaptureCommand(string SourceDirectory, string Label);
public sealed record ContentPruneQuery(int KeepCount, int KeepDays);
public sealed record ContentPruneCommand(int KeepCount, int KeepDays, string ExpectedRevision);
public sealed record ContentOfflineQuery(string Identity);
public sealed record ContentThinExportCommand(string Identity, string Destination);
public sealed record ContentThinImportCommand(string Source);
public sealed record ContentRestoreCommand(string Identity, string Destination);
public sealed record LegacyMigrationQuery(string SourceDirectory, string DestinationDirectory);
public sealed record LegacyMigrationApplyCommand(LegacyMigrationPreview Preview);
public static class ContentWorkspaceContract
{
    public static readonly Nexa.Xsr.XsrSemanticId List = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.list");
    public static readonly Nexa.Xsr.XsrSemanticId Statistics = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.statistics");
    public static readonly Nexa.Xsr.XsrSemanticId Capture = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.capture");
    public static readonly Nexa.Xsr.XsrSemanticId PrunePreview = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.prune.preview");
    public static readonly Nexa.Xsr.XsrSemanticId Prune = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.prune");
    public static readonly Nexa.Xsr.XsrSemanticId Offline = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.offline");
    public static readonly Nexa.Xsr.XsrSemanticId ExportThin = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.thin.export");
    public static readonly Nexa.Xsr.XsrSemanticId ImportThin = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.thin.import");
    public static readonly Nexa.Xsr.XsrSemanticId Restore = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.restore");
    public static readonly Nexa.Xsr.XsrSemanticId LegacyPreview = Nexa.Xsr.XsrSemanticId.Parse("setup.migration.legacy.preview");
    public static readonly Nexa.Xsr.XsrSemanticId LegacyApply = Nexa.Xsr.XsrSemanticId.Parse("setup.migration.legacy.apply");
    public static readonly Nexa.Xsr.XsrSemanticId Retention = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.retention");
    public static readonly Nexa.Xsr.XsrSemanticId SetRetention = Nexa.Xsr.XsrSemanticId.Parse("setup.backups.retention.set");
}
public sealed record ContentBackupRetentionPolicy(bool Enabled = false, int KeepCount = 32, int KeepDays = 90);
public sealed record ContentBackupRetentionQuery;
public sealed record ContentBackupRetentionCommand(ContentBackupRetentionPolicy Policy);
