namespace Nexa.Services.Logging;

/// <summary>Curated support facts. Credentials, filenames and free-form error text are excluded.</summary>
public sealed record DiagnosticBundleSnapshot(string NexaVersion, string OperatingSystem, string Architecture)
{
    public int? JavaMajor { get; init; }
    public string? MinecraftVersion { get; init; }
    public string? Loader { get; init; }
    public string? CrashClassification { get; init; }
    public IReadOnlyList<string> PreflightIssueCodes { get; init; } = [];
    public IReadOnlyList<DiagnosticModIdentity> Mods { get; init; } = [];
    public bool? InventoryComplete { get; init; }
    public int? IntegrityVerifiedFiles { get; init; }
    public int? IntegrityFailedFiles { get; init; }
    public int? FailedTasks { get; init; }
    public IReadOnlyList<LogEntry> Logs { get; init; } = [];
}

public sealed record DiagnosticModIdentity(string Id, string Version, bool Enabled);

public enum DiagnosticOperationOutcome { Started, Entered, Completed, Rejected, Cancelled, Failed, Unfinished }
public sealed record DiagnosticOperationFacts(string Name, string Stage, DiagnosticOperationOutcome Outcome)
{
    public DiagnosticInstanceContext? Instance { get; init; }
}
