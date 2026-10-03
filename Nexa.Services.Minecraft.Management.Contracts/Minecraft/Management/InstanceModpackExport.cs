using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceExportCategory(string Id, string Label, int FileCount, long Bytes);
public sealed record InstanceModpackExportPreview(string InstanceDirectory, IReadOnlyList<InstanceExportCategory> Categories);
public sealed record InstanceModpackExportQuery(string InstanceDirectory);
public sealed record InstanceModpackExportCommand(string InstanceDirectory, string DestinationPath, string Name, string Version,
    IReadOnlyList<string> Categories);
public static class InstanceModpackExportContract
{
    public static readonly XsrSemanticId Preview = XsrSemanticId.Parse("minecraft.instance.modpack.export-preview");
    public static readonly XsrSemanticId Export = XsrSemanticId.Parse("minecraft.instance.modpack.export");
}
