using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

public enum InstallDraftChangeKind { Reset, SetChosen, SetVersion, SetLoader, SetBuild, ClearBuilds, AddAddon, RemoveAddon, ClearAddons }
public sealed record InstallDraftChange(InstallDraftChangeKind Kind, string? Value = null, InstallLoader? Loader = null, bool Chosen = false);
public sealed record MinecraftInstallDraftSnapshot(bool Chosen, string GameVersion, InstallLoader? PrimaryLoader,
    IReadOnlyDictionary<InstallLoader, string> Builds, IReadOnlyList<InstallLoader> Addons)
{
    public static MinecraftInstallDraftSnapshot Empty { get; } = new(false, "", null,
        System.Collections.Frozen.FrozenDictionary<InstallLoader, string>.Empty, []);
}
public static class MinecraftInstallDraftContract
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.install.draft");
    public static readonly XsrSemanticId Change = XsrSemanticId.Parse("minecraft.install.draft.change");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<MinecraftInstallDraftSnapshot>(StateKey, "Nexa.Services.Minecraft.Install");
}
