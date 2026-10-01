using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop;

internal sealed class DesktopFunctionPatches
{
    internal static readonly XsrSemanticId ProjectTitleTarget = XsrSemanticId.Parse("ui.resource.project-title.v1");
    internal XsrFunctionPatchRuntime Runtime { get; } = new(ProjectTitleTarget);
    internal XsrFunctionPatchPoint ProjectTitle { get; }
    internal XsrFunctionPatchAdmission Admission { get; }
    internal DesktopFunctionPatches()
    {
        ProjectTitle = Runtime.Resolve(ProjectTitleTarget);
        Admission = new(Runtime, ProjectTitleTarget);
    }
}
