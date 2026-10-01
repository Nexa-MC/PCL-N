using Nexa.Xsr;

namespace Nexa.Services.Minecraft;

/// <summary>Stable library presentation and directory contracts, independent of the service.</summary>
public static class MinecraftLibraryContract
{
    public const string SettingKey = "MinecraftLibrary";
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.library");
    public static string OfficialDirectory { get; } = Nexa.Core.PathIdentity.Normalize(new Files.DefaultMinecraftRootProvider().ResolveRoot());
}
