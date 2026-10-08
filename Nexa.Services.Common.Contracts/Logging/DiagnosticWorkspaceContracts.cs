namespace Nexa.Services.Logging;

public sealed record DurableDiagnosticEntry(DateTimeOffset Timestamp, string Module, string Operation, string Stage, DiagnosticOperationOutcome Outcome)
{
    public DiagnosticInstanceContext? Instance { get; init; }
}
public sealed record DiagnosticInstanceContext(string ScopeHash, Guid SessionId)
{
    public int? ExitCode { get; init; }
    public string? FailureCode { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long? LaunchDurationMilliseconds { get; init; }
}
public sealed record DiagnosticRawPreview(string Revision, string RedactedText, int Utf8Bytes, bool Truncated);
public enum DiagnosticAiDataScope { StructuredFacts, RedactedRawText }
public enum DiagnosticAiReasoning { Provider, Low, Medium, High }
public sealed record DiagnosticAiRequest(Uri Endpoint, string Model, string ApiKey, DiagnosticAiDataScope DataScope,
    string PreviewRevision, int MaximumInputBytes, int MaximumOutputTokens)
{
    public DiagnosticAiReasoning Reasoning { get; init; }
}
public sealed record DiagnosticAiSuggestion(string Text, int InputBytes, int? ReportedOutputTokens);

public sealed record DurableDiagnosticHistoryQuery;
public sealed record DurableInstanceDiagnosticHistoryQuery(string InstanceDirectory);
public sealed record DiagnosticRawExportCommand(string Destination, DiagnosticRawPreview Preview, string ExpectedRevision);
public sealed record DiagnosticAiSuggestionQuery(DiagnosticAiRequest Request, DiagnosticRawPreview Preview, DiagnosticAiDataScope ApprovedScope);
public static class DiagnosticWorkspaceContract
{
    public static readonly Nexa.Xsr.XsrSemanticId History = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.history");
    public static readonly Nexa.Xsr.XsrSemanticId InstanceHistory = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.history.instance");
    public static readonly Nexa.Xsr.XsrSemanticId RawExport = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.raw.export");
    public static readonly Nexa.Xsr.XsrSemanticId AiSuggestion = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.ai.suggestion");
    public static readonly Nexa.Xsr.XsrSemanticId RawPreview = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.raw.preview");
    public static readonly Nexa.Xsr.XsrSemanticId FactsPreview = Nexa.Xsr.XsrSemanticId.Parse("logging.diagnostics.facts.preview");
}
public sealed record DiagnosticRawPreviewQuery(string RawText);
public sealed record DiagnosticFactsPreviewQuery(IReadOnlyList<DurableDiagnosticEntry> Entries);
