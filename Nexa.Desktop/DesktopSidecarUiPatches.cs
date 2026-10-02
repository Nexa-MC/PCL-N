using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop;

internal static class SidecarUiPresentationState
{
    internal static readonly XsrSemanticId Captions = XsrSemanticId.Parse("ui.sidecar.captions");
    internal static void Declare(XsrStateStoreBuilder builder) => builder.Cell<XsrUiPatchSnapshot>(Captions, "Nexa.Desktop.Sidecar");
}

internal sealed class DesktopSidecarUiPatches
{
    internal static readonly XsrSemanticId SearchLabel = XsrSemanticId.Parse("ui.resources.search-label.v1");
    internal XsrUiPatchAdmission Admission { get; }
    internal XsrStateId State { get; }
    internal int SearchIndex { get; }
    internal DesktopSidecarUiPatches(XsrStateStore store)
    {
        State = store.Resolve(SidecarUiPresentationState.Captions);
        var runtime = new XsrUiPatchRuntime(store, State, new XsrUiCaptionTarget(SearchLabel, 16));
        SearchIndex = runtime.Resolve(SearchLabel);
        Admission = new(runtime, SearchLabel);
    }
}
