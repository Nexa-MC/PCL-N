using System.Collections;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    private MinecraftInstallDraftSnapshot InstallDraft => _store.TryResolve(MinecraftInstallDraftContract.StateKey, out var id)
        && _store.ReadAppliedValue(id) is MinecraftInstallDraftSnapshot snapshot ? snapshot : MinecraftInstallDraftSnapshot.Empty;
    private void ChangeDraft(InstallDraftChange change)
    {
        if (_installCatalogCommands?.TryResolve(MinecraftInstallDraftContract.Change, out var route) == true)
            _ = _installCatalogCommands.Dispatch(route, change, cancellationToken: _lifetimeCancellation.Token);
    }
    private bool _installGameChosen { get => InstallDraft.Chosen; set => ChangeDraft(new(InstallDraftChangeKind.SetChosen, Chosen: value)); }
    private string _selectedInstallVersion { get => InstallDraft.GameVersion; set => ChangeDraft(new(InstallDraftChangeKind.SetVersion, value)); }
    private string _selectedInstallLoader { get => InstallDraft.PrimaryLoader?.ToString() ?? "原版 Minecraft"; set => ChangeDraft(new(InstallDraftChangeKind.SetLoader, Loader: ParseInstallLoader(value))); }
    private DraftBuildView _selectedInstallBuilds => new(this);
    private DraftAddonView _selectedInstallAddons => new(this);
    private static string AddonLabel(InstallLoader loader) => loader == InstallLoader.FabricApi ? "Fabric API" : loader == InstallLoader.Qsl ? "QSL" : loader.ToString();

    private sealed class DraftBuildView(LaunchPageController owner) : IReadOnlyDictionary<InstallLoader, string>
    {
        private IReadOnlyDictionary<InstallLoader, string> ValuesNow => owner.InstallDraft.Builds;
        public string this[InstallLoader loader] { get => ValuesNow[loader]; set => owner.ChangeDraft(new(InstallDraftChangeKind.SetBuild, value, loader)); }
        public int Count => ValuesNow.Count;
        public IEnumerable<InstallLoader> Keys => ValuesNow.Keys;
        public IEnumerable<string> Values => ValuesNow.Values;
        public bool ContainsKey(InstallLoader loader) => ValuesNow.ContainsKey(loader);
        public bool TryGetValue(InstallLoader loader, out string value) => ValuesNow.TryGetValue(loader, out value!);
        public void Clear() => owner.ChangeDraft(new(InstallDraftChangeKind.ClearBuilds));
        public IEnumerator<KeyValuePair<InstallLoader, string>> GetEnumerator() => ValuesNow.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class DraftAddonView(LaunchPageController owner) : IEnumerable<string>
    {
        public int Count => owner.InstallDraft.Addons.Count;
        public bool Contains(string label) => owner.InstallDraft.Addons.Any(x => AddonLabel(x) == label);
        public bool Add(string label) { if (Contains(label)) return false; owner.ChangeDraft(new(InstallDraftChangeKind.AddAddon, Loader: ParseInstallLoader(label))); return true; }
        public bool Remove(string label) { if (!Contains(label)) return false; owner.ChangeDraft(new(InstallDraftChangeKind.RemoveAddon, Loader: ParseInstallLoader(label))); return true; }
        public void Clear() => owner.ChangeDraft(new(InstallDraftChangeKind.ClearAddons));
        public IEnumerator<string> GetEnumerator() => owner.InstallDraft.Addons.Select(AddonLabel).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
