using System.Collections.Frozen;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Owns installation choices. Desktop publishes intent and projects this snapshot.</summary>
public sealed class MinecraftInstallDraftService(XsrStateStore store)
{
    private readonly object _gate = new();
    private MinecraftInstallDraftSnapshot _current = MinecraftInstallDraftSnapshot.Empty;

    public XsrResult Apply(InstallDraftChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Loader is { } candidate && !Enum.IsDefined(candidate))
            return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("minecraft.install.draft.invalid_loader"), "加载器无效。"));
        lock (_gate)
        {
            var next = _current;
            switch (change.Kind)
            {
                case InstallDraftChangeKind.Reset: next = MinecraftInstallDraftSnapshot.Empty; break;
                case InstallDraftChangeKind.SetChosen: next = next with { Chosen = change.Chosen }; break;
                case InstallDraftChangeKind.SetVersion: next = next with { GameVersion = change.Value ?? "" }; break;
                case InstallDraftChangeKind.SetLoader: next = next with { PrimaryLoader = change.Loader }; break;
                case InstallDraftChangeKind.ClearBuilds: next = next with { Builds = FrozenDictionary<InstallLoader, string>.Empty }; break;
                case InstallDraftChangeKind.SetBuild when change.Loader is { } loader && !string.IsNullOrWhiteSpace(change.Value):
                    var builds = next.Builds.ToDictionary(); builds[loader] = change.Value;
                    next = next with { Builds = builds.ToFrozenDictionary() }; break;
                case InstallDraftChangeKind.AddAddon when change.Loader is { } addon:
                    if ((addon == InstallLoader.FabricApi || addon == InstallLoader.OptiFabric) && next.PrimaryLoader != InstallLoader.Fabric
                        || addon == InstallLoader.Qsl && next.PrimaryLoader != InstallLoader.Quilt)
                        return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("minecraft.install.draft.incompatible_addon"), "请先选择兼容的加载器。"));
                    next = next with { Addons = Array.AsReadOnly(next.Addons.Append(addon).Distinct().ToArray()) }; break;
                case InstallDraftChangeKind.RemoveAddon: next = next with { Addons = Array.AsReadOnly(next.Addons.Where(x => x != change.Loader).ToArray()) }; break;
                case InstallDraftChangeKind.ClearAddons: next = next with { Addons = [] }; break;
                default: return XsrResult.Failure(new XsrError(XsrErrorKind.Rejected, XsrSemanticId.Parse("minecraft.install.draft.invalid_change"), "安装选择无效。"));
            }
            next = next with
            {
                Addons = Array.AsReadOnly(next.Addons.Where(x => x == InstallLoader.Qsl
                ? next.PrimaryLoader == InstallLoader.Quilt : next.PrimaryLoader == InstallLoader.Fabric).ToArray())
            };
            store.Publish(store.Resolve(MinecraftInstallDraftContract.StateKey), next);
            _current = next;
            return XsrResult.Success();
        }
    }
}
