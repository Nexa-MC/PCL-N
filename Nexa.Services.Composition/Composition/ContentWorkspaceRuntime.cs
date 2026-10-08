using Nexa.Services.Logging;
using Nexa.Services.Setup;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

/// <summary>Register before sealing the foundation routers; all mutations retain dispatch observation.</summary>
public static class ContentWorkspaceRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries,
        ContentBackupService backups, LegacyMigrationService migration, DurableDiagnosticHistorySink history, DiagnosticAiService? ai = null)
    {
        queries.Register<ContentBackupListQuery, IReadOnlyList<ContentBackupManifest>>(ContentWorkspaceContract.List,
            (_, token) => QueryAsync(() => backups.ListAsync(token), token));
        queries.Register<ContentStoreStatisticsQuery, ContentStoreStatistics>(ContentWorkspaceContract.Statistics,
            (_, token) => QueryAsync(() => backups.StatisticsAsync(token), token));
        queries.Register<ContentBackupRetentionQuery, ContentBackupRetentionPolicy>(ContentWorkspaceContract.Retention,
            (_, token) => QueryAsync(() => backups.ReadRetentionAsync(token), token));
        queries.Register<ContentPruneQuery, ContentPrunePreview>(ContentWorkspaceContract.PrunePreview,
            (query, token) => QueryAsync(() => backups.PreviewPruneAsync(query.KeepCount, query.KeepDays, token), token));
        queries.Register<ContentOfflineQuery, OfflineReadinessReport>(ContentWorkspaceContract.Offline,
            (query, token) => QueryAsync(() => backups.VerifyOfflineAsync(query.Identity, token), token));
        queries.Register<LegacyMigrationQuery, LegacyMigrationPreview>(ContentWorkspaceContract.LegacyPreview,
            (query, token) => QueryAsync(() => migration.PreviewAsync(query.SourceDirectory, query.DestinationDirectory, token), token));
        queries.Register<DurableDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(DiagnosticWorkspaceContract.History,
            (_, token) => QueryAsync(() => history.ReadAsync(token), token));
        queries.Register<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(DiagnosticWorkspaceContract.InstanceHistory,
            (query, token) => QueryAsync(() => history.ReadForInstanceAsync(query.InstanceDirectory, token), token));
        queries.Register<DiagnosticRawPreviewQuery, DiagnosticRawPreview>(DiagnosticWorkspaceContract.RawPreview,
            (query, token) => QueryAsync(() => Task.FromResult(DiagnosticRawWorkspace.Preview(query.RawText)), token));
        queries.Register<DiagnosticFactsPreviewQuery, DiagnosticRawPreview>(DiagnosticWorkspaceContract.FactsPreview,
            (query, token) => QueryAsync(() => Task.FromResult(DiagnosticAiService.PreviewFacts(query.Entries)), token));
        commands.Register<ContentBackupCaptureCommand>(ContentWorkspaceContract.Capture,
            (command, token) => CommandAsync(async () => { await backups.CaptureAsync(command.SourceDirectory, command.Label, token).ConfigureAwait(false); }, token));
        commands.Register<ContentPruneCommand>(ContentWorkspaceContract.Prune,
            (command, token) => CommandAsync(() => backups.ApplyPruneAsync(command.KeepCount, command.KeepDays, command.ExpectedRevision, token), token));
        commands.Register<ContentBackupRetentionCommand>(ContentWorkspaceContract.SetRetention,
            (command, token) => CommandAsync(() => backups.SetRetentionAsync(command.Policy, token), token));
        commands.Register<ContentThinExportCommand>(ContentWorkspaceContract.ExportThin,
            (command, token) => CommandAsync(() => backups.ExportThinAsync(command.Identity, command.Destination, token), token));
        commands.Register<ContentThinImportCommand>(ContentWorkspaceContract.ImportThin,
            (command, token) => CommandAsync(async () => { await backups.ImportThinAsync(command.Source, token).ConfigureAwait(false); }, token));
        commands.Register<ContentRestoreCommand>(ContentWorkspaceContract.Restore,
            (command, token) => CommandAsync(() => backups.RestoreAsync(command.Identity, command.Destination, token), token));
        commands.Register<LegacyMigrationApplyCommand>(ContentWorkspaceContract.LegacyApply,
            (command, token) => CommandAsync(() => migration.ApplyAsync(command.Preview, token), token));
        commands.Register<DiagnosticRawExportCommand>(DiagnosticWorkspaceContract.RawExport,
            (command, token) => CommandAsync(() => DiagnosticRawWorkspace.ExportAsync(command.Destination, command.Preview, command.ExpectedRevision, token), token));
        if (ai is not null)
            queries.Register<DiagnosticAiSuggestionQuery, DiagnosticAiSuggestion>(DiagnosticWorkspaceContract.AiSuggestion,
                (query, token) => QueryAsync(() => ai.SuggestAsync(query.Request, query.Preview, query.ApprovedScope, token), token));
    }

    private static async ValueTask<XsrResult<T>> QueryAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        try { return XsrResult.Success(await Task.Run(action, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure<T>(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (Expected(error)) { return XsrResult.Failure<T>(Failure(error)); }
    }
    private static async ValueTask<XsrResult> CommandAsync(Func<Task> action, CancellationToken token)
    {
        try { await Task.Run(action, token).ConfigureAwait(false); return XsrResult.Success(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (Expected(error)) { return XsrResult.Failure(Failure(error)); }
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
        or FormatException or System.Text.Json.JsonException or HttpRequestException or TimeoutException or OperationCanceledException or KeyNotFoundException or NullReferenceException;
    private static XsrError Failure(Exception error) => new(XsrErrorKind.Rejected, XsrSemanticId.Parse("setup.workspace.rejected"), LogRedactor.Redact(error.Message));
}
