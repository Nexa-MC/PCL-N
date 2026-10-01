using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal static class ResourceCaptions
{
    [XsrFunctionPatch("ui.resource.project-title.v1")]
    internal static string ProjectTitle(XsrFunctionPatchRuntime runtime, XsrFunctionPatchPoint point, string title)
    {
        return title;
    }
}
