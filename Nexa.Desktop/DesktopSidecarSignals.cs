using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop;

/// <summary>Explicit, non-secret presentation grants owned by the Desktop composition.</summary>
internal sealed class DesktopSidecarSignals
{
    private static readonly XsrSemanticId Search = XsrSemanticId.Parse("ui.resources.search.v1");
    private static readonly XsrSemanticId Completed = XsrSemanticId.Parse("event.resources.search-completed.v1");
    private readonly XsrSignalRuntime _runtime = new(new(Search, XsrSignalKind.Intent, AllowsCatch: true),
        new(Completed, XsrSignalKind.Event));
    private readonly XsrSignalPoint _search;
    private readonly XsrSignalPoint _completed;
    internal XsrSignalAdmission Admission { get; }

    internal DesktopSidecarSignals()
    {
        _search = _runtime.Resolve(Search);
        _completed = _runtime.Resolve(Completed);
        Admission = new(_runtime, Search, Completed);
    }

    internal bool CatchResourceSearch() => _runtime.Emit(_search, "search");
    internal void ResourceSearchCompleted(bool success) => _runtime.Emit(_completed, success ? "success" : "failure");
}
