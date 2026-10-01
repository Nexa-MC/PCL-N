

using Nexa.Xsr;


namespace Nexa.Services.Minecraft;

public static class MinecraftLibraryRoutes
{
    public static readonly XsrSemanticId Refresh = XsrSemanticId.Parse("minecraft.library.refresh");
    public static readonly XsrSemanticId Directory = XsrSemanticId.Parse("minecraft.library.directory");
    public static readonly XsrSemanticId Forget = XsrSemanticId.Parse("minecraft.library.forget");
    public static readonly XsrSemanticId Delete = XsrSemanticId.Parse("minecraft.library.delete");
    public static readonly XsrSemanticId Select = XsrSemanticId.Parse("minecraft.library.select");
    public static readonly XsrSemanticId Rename = XsrSemanticId.Parse("minecraft.library.rename");
}
