using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Install;

public enum MinecraftInstallEditKind { Unchanged, ComponentsOnly, Reinstall }
public sealed record MinecraftInstallEditPlanQuery(MinecraftInstallEditSnapshot Original, IReadOnlyList<InstallBuildSelection> Selection);
public sealed record MinecraftInstallEditPlan(MinecraftInstallEditKind Kind, IReadOnlyList<InstallLoader> ChangedLoaders, string ActionLabel);
public static class MinecraftInstallEditPlanContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.install.edit.plan");
}
