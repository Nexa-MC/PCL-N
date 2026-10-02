using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop;

internal static class SidecarUiPresentationState
{
    internal static readonly XsrSemanticId Captions = XsrSemanticId.Parse("ui.sidecar.captions");
    internal static readonly XsrSemanticId Modules = XsrSemanticId.Parse("ui.sidecar.modules");
    internal static void Declare(XsrStateStoreBuilder builder)
    {
        builder.Cell<XsrUiPatchSnapshot>(Captions, "Nexa.Desktop.Sidecar");
        builder.Cell<XsrUiModuleSnapshot>(Modules, "Nexa.Desktop.Sidecar");
    }
}

internal sealed class DesktopSidecarUiPatches
{
    internal static readonly XsrSemanticId SearchLabel = XsrSemanticId.Parse("ui.resources.search-label.v1");
    internal static readonly XsrSemanticId ResourceCard = XsrSemanticId.Parse("ui.resources.extension-card.v1");
    internal XsrUiModuleAdmission ModuleAdmission { get; }
    internal XsrStateId ModuleState { get; }
    internal int CardIndex { get; }
    internal XsrUiPatchAdmission Admission { get; }
    internal XsrStateId State { get; }
    internal int SearchIndex { get; }
    internal DesktopSidecarUiPatches(XsrStateStore store)
    {
        State = store.Resolve(SidecarUiPresentationState.Captions);
        var runtime = new XsrUiPatchRuntime(store, State, new XsrUiCaptionTarget(SearchLabel, 16));
        SearchIndex = runtime.Resolve(SearchLabel);
        Admission = new(runtime, SearchLabel);
        ModuleState = store.Resolve(SidecarUiPresentationState.Modules);
        var modules = new XsrUiModuleRuntime(store, ModuleState, ResourceCard);
        CardIndex = modules.Resolve(ResourceCard);
        ModuleAdmission = new(modules, ResourceCard);
    }
}
