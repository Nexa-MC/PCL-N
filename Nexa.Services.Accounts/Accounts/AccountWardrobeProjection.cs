using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Accounts;

public sealed partial class AccountWardrobeService
{
    private async Task<AccountWardrobeSnapshot> ProjectAsync(AccountWardrobeCapture capture,
        IReadOnlyList<AccountWardrobeCape> capes, IReadOnlyList<LittleSkinClosetItem> closetSkins,
        AccountWardrobeCapeState capeState, string? providerStatus, Operation operation)
    {
        var profiles = accounts.GetViews().OrderByDescending(view => view.Index == capture.Identity.Index).Take(64).ToArray();
        var index = Array.FindIndex(profiles, view => view.Index == capture.Identity.Index);
        var current = await _textures.ResolveAsync(capture.View, operation.Token).ConfigureAwait(false);
        var resolved = profiles.Select(view => new AccountWardrobeResolvedTextures(
            WardrobeTextureResolver.SafeAddress(view.SkinAddress), null, false, null, null)).ToArray();
        if (index >= 0) resolved[index] = current;
        using (var historyBudget = CancellationTokenSource.CreateLinkedTokenSource(operation.Token))
        {
            historyBudget.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, profiles.Length).Where(position => position != index),
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = historyBudget.Token },
                    async (position, token) => resolved[position] = await _textures.ResolveReferencesAsync(profiles[position], token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!operation.Token.IsCancellationRequested) { }
        }
        operation.Admit();
        if (capture.Profile.Kind == LaunchProfileKind.Microsoft && capes.FirstOrDefault(cape => cape.IsActive) is { } active)
        {
            var capeImage = await _textures.ReadImageAsync(active.TextureAddress, AccountWardrobeTextureKind.Cape,
                current.IsSlim, operation.Token).ConfigureAwait(false);
            current = current with { CapeAddress = active.TextureAddress, Cape = capeImage };
        }
        operation.Admit();
        var now = DateTimeOffset.UtcNow;
        List<AccountWardrobeHistoryEntry> entries = [];
        for (int position = 0; position < profiles.Length; position++)
        {
            var appearance = position == index ? current : resolved[position];
            string profileKey = WardrobeHistoryStore.ProfileKey(profiles[position]);
            if (WardrobeTextureResolver.SafeAddress(appearance.SkinAddress) is { } skinAddress)
                entries.Add(new(profileKey, profiles[position].Username, AccountWardrobeTextureKind.Skin,
                    skinAddress, appearance.IsSlim, now));
            if (WardrobeTextureResolver.SafeAddress(appearance.CapeAddress) is { } capeAddress)
                entries.Add(new(profileKey, profiles[position].Username, AccountWardrobeTextureKind.Cape,
                    capeAddress, appearance.IsSlim, now));
        }
        await _history.RecordAsync(entries, operation.Token).ConfigureAwait(false);
        var history = await _history.LoadAsync(operation.Token).ConfigureAwait(false);
        operation.Admit();
        string currentKey = WardrobeHistoryStore.ProfileKey(capture.View);
        var historySkins = history.Where(entry => entry.Kind == AccountWardrobeTextureKind.Skin
            && !string.Equals(entry.Address, current.SkinAddress, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new AccountWardrobeCard(CardId("history_skin", entry.Address), entry.DisplayName,
                string.Equals(entry.ProfileKey, currentKey, StringComparison.OrdinalIgnoreCase) ? "此前使用" : "其他档案",
                new(entry.Address, null, entry.IsSlim, null, null),
                capture.Profile.Kind == LaunchProfileKind.Microsoft || capture.Profile.Kind == LaunchProfileKind.NCloud && _cloud?.CanManage(capture.View) == true));
        var skinCards = closetSkins.Where(item => item.TextureId > 0).Take(1024).Select(item =>
            new AccountWardrobeCard(CardId("closet_skin", item.TextureId.ToString(CultureInfo.InvariantCulture)), item.Name,
                "LittleSkin 衣柜", new(item.TextureAddress, null,
                    string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase), null, null), true, item.TextureId))
            .Concat(historySkins).DistinctBy(card => card.Id, StringComparer.Ordinal)
            .DistinctBy(card => card.Appearance.SkinAddress, StringComparer.OrdinalIgnoreCase).ToArray();
        AccountWardrobeCard[] capeCards;
        if (capture.Profile.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin)
            capeCards = capes.Select(cape => new AccountWardrobeCard(CardId("owned_cape", cape.Id), cape.Name,
                cape.IsActive ? "当前使用" : capture.Profile.Kind == LaunchProfileKind.Microsoft ? "正版账户已获得" : "LittleSkin 衣柜",
                current with { CapeAddress = cape.TextureAddress, Cape = cape.IsActive ? current.Cape : null },
                !cape.IsActive, capture.Profile.Kind == LaunchProfileKind.LittleSkin && long.TryParse(cape.Id, out long tid) ? tid : null, cape.Id)
            { Kind = AccountWardrobeTextureKind.Cape, IsActive = cape.IsActive }).ToArray();
        else
            capeCards = history.Where(entry => entry.Kind == AccountWardrobeTextureKind.Cape)
                .Select(entry => new AccountWardrobeCard(CardId("history_cape", entry.Address), entry.DisplayName,
                    string.Equals(entry.ProfileKey, currentKey, StringComparison.OrdinalIgnoreCase) ? "当前或此前使用" : "其他档案",
                    current with { CapeAddress = entry.Address, Cape = null }, false)
                { Kind = AccountWardrobeTextureKind.Cape }).ToArray();
        string? unavailable = Unavailable(capture);
        return new(capture.Identity, capture.View, unavailable is null,
            unavailable is null && capture.Profile.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin,
            unavailable, capes)
        {
            Current = current,
            Skins = Array.AsReadOnly(skinCards),
            CapeCards = Array.AsReadOnly(capeCards),
            CapeState = capeState,
            ProviderStatus = providerStatus,
            ManageUri = ManageAddress(capture.Profile)
        };
    }

    private static string CardId(string source, string identity) => source + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));

    private static string? ManageAddress(LaunchProfile profile)
    {
        if (profile.Kind == LaunchProfileKind.LittleSkin) return "https://littleskin.cn/user";
        if (profile.Kind != LaunchProfileKind.ThirdParty || WardrobeTextureResolver.SafeAddress(profile.AuthServer) is not { } safe
            || !Uri.TryCreate(safe, UriKind.Absolute, out var address)) return null;
        string path = address.AbsolutePath.TrimEnd('/');
        foreach (string suffix in new[] { "/api/yggdrasil/authserver", "/api/yggdrasil" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new UriBuilder(address) { Path = path[..^suffix.Length].TrimEnd('/') + "/user/profile" }.Uri.AbsoluteUri;
        }
        return address.AbsoluteUri;
    }

    private async Task RememberAsync(AccountWardrobeCapture capture, CancellationToken token)
    {
        // History is convenience data. Never let a failed read or write invalidate the provider result.
        var appearance = ProjectedAppearance(capture.Identity)
            ?? await _textures.ResolveAsync(capture.View, token).ConfigureAwait(false);
        if (!accounts.CaptureActiveWardrobe(capture.Identity).IsSuccess) throw new StaleWardrobeException();
        await RememberAppearanceAsync(capture.View, appearance, token).ConfigureAwait(false);
    }

    private ValueTask RememberAppearanceAsync(LaunchProfileView profile, AccountWardrobeResolvedTextures appearance,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        string key = WardrobeHistoryStore.ProfileKey(profile);
        List<AccountWardrobeHistoryEntry> entries = [];
        if (WardrobeTextureResolver.SafeAddress(appearance.SkinAddress) is { } skin)
            entries.Add(new(key, profile.Username, AccountWardrobeTextureKind.Skin, skin, appearance.IsSlim, now));
        if (WardrobeTextureResolver.SafeAddress(appearance.CapeAddress) is { } cape)
            entries.Add(new(key, profile.Username, AccountWardrobeTextureKind.Cape, cape, appearance.IsSlim, now));
        return _history.RecordAsync(entries, token);
    }
}
