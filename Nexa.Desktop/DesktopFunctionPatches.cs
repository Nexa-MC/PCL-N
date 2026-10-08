using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop;

internal sealed class DesktopFunctionPatches
{
    internal static readonly XsrSemanticId ProjectTitleTarget = XsrSemanticId.Parse("ui.resource.project-title.v1");
    internal static readonly XsrSemanticId DownloadCountTarget = XsrSemanticId.Parse("ui.resource.download-count.v1");
    internal XsrFunctionPatchRuntime Runtime { get; } = new(new XsrFunctionTarget(ProjectTitleTarget, XsrFunctionShape.String),
        new XsrFunctionTarget(DownloadCountTarget, new(XsrFunctionValueKind.Int64, XsrFunctionValueKind.Int64)));
    internal XsrFunctionPatchPoint DownloadCount { get; }
    internal XsrFunctionPatchPoint ProjectTitle { get; }
    internal XsrFunctionPatchAdmission Admission { get; }
    internal DesktopFunctionPatches()
    {
        ProjectTitle = Runtime.Resolve(ProjectTitleTarget);
        DownloadCount = Runtime.Resolve(DownloadCountTarget);
        Admission = new(Runtime, ProjectTitleTarget, DownloadCountTarget);
    }
}
