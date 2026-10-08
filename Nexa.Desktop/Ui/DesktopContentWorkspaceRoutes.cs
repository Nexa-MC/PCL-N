using Nexa.Services.Logging;
using Nexa.Services.Setup;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

/// <summary>Typed presentation adapter; all effects retain foundation dispatch contracts.</summary>
internal sealed class DesktopContentWorkspaceRoutes(XsrQueryRouter queries, XsrCommandRouter commands)
{
    internal Task<IReadOnlyList<ContentBackupManifest>> ListAsync(CancellationToken token) => Query<ContentBackupListQuery, IReadOnlyList<ContentBackupManifest>>(ContentWorkspaceContract.List, new(), token);
    internal Task<ContentStoreStatistics> StatisticsAsync(CancellationToken token) => Query<ContentStoreStatisticsQuery, ContentStoreStatistics>(ContentWorkspaceContract.Statistics, new(), token);
    internal Task<ContentBackupRetentionPolicy> ReadRetentionAsync(CancellationToken token) => Query<ContentBackupRetentionQuery, ContentBackupRetentionPolicy>(ContentWorkspaceContract.Retention, new(), token);
    internal Task SetRetentionAsync(ContentBackupRetentionPolicy policy, CancellationToken token) => Command(ContentWorkspaceContract.SetRetention, new ContentBackupRetentionCommand(policy), token);
    internal Task<ContentPrunePreview> PreviewPruneAsync(int count, int days, CancellationToken token) => Query<ContentPruneQuery, ContentPrunePreview>(ContentWorkspaceContract.PrunePreview, new(count, days), token);
    internal Task ApplyPruneAsync(int count, int days, string revision, CancellationToken token) => Command(ContentWorkspaceContract.Prune, new ContentPruneCommand(count, days, revision), token);
    internal Task<OfflineReadinessReport> VerifyOfflineAsync(string identity, CancellationToken token) => Query<ContentOfflineQuery, OfflineReadinessReport>(ContentWorkspaceContract.Offline, new(identity), token);
    internal Task CaptureAsync(string source, string label, CancellationToken token) => Command(ContentWorkspaceContract.Capture, new ContentBackupCaptureCommand(source, label), token);
    internal Task ImportThinAsync(string source, CancellationToken token) => Command(ContentWorkspaceContract.ImportThin, new ContentThinImportCommand(source), token);
    internal Task ExportThinAsync(string identity, string destination, CancellationToken token) => Command(ContentWorkspaceContract.ExportThin, new ContentThinExportCommand(identity, destination), token);
    internal Task RestoreAsync(string identity, string destination, CancellationToken token) => Command(ContentWorkspaceContract.Restore, new ContentRestoreCommand(identity, destination), token);
    internal Task<LegacyMigrationPreview> PreviewAsync(string source, string destination, CancellationToken token) => Query<LegacyMigrationQuery, LegacyMigrationPreview>(ContentWorkspaceContract.LegacyPreview, new(source, destination), token);
    internal Task ApplyAsync(LegacyMigrationPreview preview, CancellationToken token) => Command(ContentWorkspaceContract.LegacyApply, new LegacyMigrationApplyCommand(preview), token);
    internal Task<IReadOnlyList<DurableDiagnosticEntry>> ReadAsync(CancellationToken token) => Query<DurableDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(DiagnosticWorkspaceContract.History, new(), token);
    internal Task<DiagnosticRawPreview> PreviewRawAsync(string raw, CancellationToken token) => Query<DiagnosticRawPreviewQuery, DiagnosticRawPreview>(DiagnosticWorkspaceContract.RawPreview, new(raw), token);
    internal Task<DiagnosticRawPreview> PreviewFactsAsync(IReadOnlyList<DurableDiagnosticEntry> entries, CancellationToken token) => Query<DiagnosticFactsPreviewQuery, DiagnosticRawPreview>(DiagnosticWorkspaceContract.FactsPreview, new(entries), token);
    internal Task ExportRawAsync(string destination, DiagnosticRawPreview preview, string revision, CancellationToken token) => Command(DiagnosticWorkspaceContract.RawExport, new DiagnosticRawExportCommand(destination, preview, revision), token);
    internal Task<DiagnosticAiSuggestion> SuggestAsync(DiagnosticAiRequest request, DiagnosticRawPreview preview, DiagnosticAiDataScope scope, CancellationToken token) => Query<DiagnosticAiSuggestionQuery, DiagnosticAiSuggestion>(DiagnosticWorkspaceContract.AiSuggestion, new(request, preview, scope), token);
    private async Task<TReply> Query<TQuery, TReply>(XsrSemanticId semantic, TQuery query, CancellationToken token)
        where TQuery : notnull
    {
        if (!queries.TryResolve(semantic, out var route)) throw new IOException("工作区能力尚未注册。");
        var result = await queries.QueryAsync<TQuery, TReply>(route, query, cancellationToken: token).ConfigureAwait(false);
        if (!result.IsSuccess) { token.ThrowIfCancellationRequested(); throw new IOException(result.Error?.Message ?? "工作区查询失败。"); }
        return result.Value!;
    }
    private async Task Command<TCommand>(XsrSemanticId semantic, TCommand command, CancellationToken token)
        where TCommand : notnull
    {
        if (!commands.TryResolve(semantic, out var route)) throw new IOException("工作区操作尚未注册。");
        var result = await commands.Dispatch(route, command, cancellationToken: token).Completion.ConfigureAwait(false);
        if (!result.IsSuccess) { token.ThrowIfCancellationRequested(); throw new IOException(result.Error?.Message ?? "工作区操作失败。"); }
    }
}
