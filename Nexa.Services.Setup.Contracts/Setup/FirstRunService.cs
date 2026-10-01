

using Nexa.Xsr;

namespace Nexa.Services.Setup;

public sealed record FirstRunStatus(bool Required, string DataDirectory, bool LocationLocked)
{
    public bool TelemetryRequired { get; init; }
}
public sealed record FirstRunQuery;
public sealed record FirstRunCompleteCommand(string DataDirectory, bool Telemetry);
public static class FirstRunContract
{
    public static readonly XsrSemanticId Status = XsrSemanticId.Parse("setup.status");
    public static readonly XsrSemanticId Complete = XsrSemanticId.Parse("setup.complete");
}
