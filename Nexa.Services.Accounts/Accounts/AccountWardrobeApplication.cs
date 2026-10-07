using Nexa.Xsr;

namespace Nexa.Services.Accounts;

public sealed partial class AccountWardrobeService
{
    public async ValueTask<XsrResult> ApplyCardAsync(AccountWardrobeApplyCardCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(command.Identity);
            var card = RequireCard(command.Identity, command.CardId);
            if (!card.CanApply) throw new InvalidDataException("此材质不能应用到当前账户。");
            if (card.Kind == AccountWardrobeTextureKind.Cape)
                return await SetCapeAsync(new(command.Identity, card.CapeId), cancellationToken).ConfigureAwait(false);
            DemandProvider(capture);
            if (capture.Profile.Kind == LaunchProfileKind.LittleSkin && card.TextureId is { } tid)
                return await ApplyLittleSkinAsync(capture, tid, card.Title, card.Appearance.SkinAddress,
                    LittleSkinTextureKind.Skin, card.Appearance.IsSlim, cancellationToken).ConfigureAwait(false);
            return await ApplySkinAddressAsync(capture, card.Appearance.SkinAddress, card.Appearance.IsSlim,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Recoverable(error)) { return Reject(error); }
    }

    public async ValueTask<XsrResult> ApplyPublicAsync(AccountWardrobeApplyPublicCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(command.Identity);
            DemandProvider(capture);
            if (!Enum.IsDefined(command.Kind) || command.TextureId <= 0 || command.SiteId != "littleskin")
                throw new InvalidDataException("所选皮肤站材质无效。");
            if (command.Kind == AccountWardrobeTextureKind.Cape && capture.Profile.Kind != LaunchProfileKind.LittleSkin)
                throw new InvalidDataException("正版与 N Cloud 档案不能直接使用 LittleSkin 公共披风。");
            WardrobeCatalogItem item;
            using (var operation = new Operation(accounts, capture, cancellationToken))
            {
                var resolved = await _catalog.ResolveItemAsync(command.SiteId, command.TextureId,
                    operation.Token).ConfigureAwait(false);
                operation.Admit();
                if (!resolved.IsSuccess) return XsrResult.Failure(resolved.Error!);
                item = resolved.Value;
                if ((item.Kind == WardrobeCatalogKind.Cape) != (command.Kind == AccountWardrobeTextureKind.Cape))
                    throw new InvalidDataException("所选材质类型与皮肤站返回内容不一致，请刷新皮肤库。");
            }
            if (capture.Profile.Kind == LaunchProfileKind.LittleSkin)
                return await ApplyLittleSkinAsync(capture, item.TextureId, item.Name, item.TextureAddress,
                    command.Kind == AccountWardrobeTextureKind.Cape ? LittleSkinTextureKind.Cape : LittleSkinTextureKind.Skin,
                    string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
            bool slim = string.Equals(item.Model, "alex", StringComparison.OrdinalIgnoreCase);
            if (capture.Profile.Kind == LaunchProfileKind.NCloud)
                return await ApplyCloudSiteAsync(capture, command.SiteId, item.TextureId, slim,
                    cancellationToken).ConfigureAwait(false);
            return await ApplySkinAddressAsync(capture, item.TextureAddress, slim, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Recoverable(error)) { return Reject(error); }
    }

    public async ValueTask<XsrResult<AccountWardrobeResolvedTextures>> ReadTextureAsync(AccountWardrobeTextureQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(query.Identity);
            if (!Enum.IsDefined(query.Kind)) throw new InvalidDataException("材质类型无效。");
            using var operation = new Operation(accounts, capture, cancellationToken);
            AccountWardrobeResolvedTextures appearance;
            if (query.CardId is not null)
            {
                if (query.SiteId is not null || query.TextureId != 0)
                    throw new InvalidDataException("材质预览请求只能包含一个来源。");
                var card = RequireCard(query.Identity, query.CardId);
                if (card.Kind != query.Kind) throw new InvalidDataException("材质预览类型与卡片不一致。");
                appearance = card.Appearance;
            }
            else
            {
                if (query.SiteId != "littleskin" || query.TextureId <= 0)
                    throw new InvalidDataException("所选皮肤站材质无效。");
                var item = await _catalog.ResolveItemAsync(query.SiteId, query.TextureId, operation.Token).ConfigureAwait(false);
                operation.Admit();
                if (!item.IsSuccess) return XsrResult.Failure<AccountWardrobeResolvedTextures>(item.Error!);
                if ((item.Value.Kind == WardrobeCatalogKind.Cape) != (query.Kind == AccountWardrobeTextureKind.Cape))
                    throw new InvalidDataException("材质预览类型与皮肤站返回内容不一致。");
                bool slim = string.Equals(item.Value.Model, "alex", StringComparison.OrdinalIgnoreCase);
                appearance = query.Kind == AccountWardrobeTextureKind.Skin
                    ? new(item.Value.TextureAddress, null, slim, null, null)
                    : new(null, item.Value.TextureAddress, false, null, null);
            }
            var skin = appearance.Skin;
            var cape = appearance.Cape;
            if (skin is null && appearance.SkinAddress is { } skinAddress)
                skin = await _textures.ReadImageAsync(skinAddress, AccountWardrobeTextureKind.Skin,
                    appearance.IsSlim, operation.Token).ConfigureAwait(false);
            operation.Admit();
            if (cape is null && appearance.CapeAddress is { } capeAddress)
                cape = await _textures.ReadImageAsync(capeAddress, AccountWardrobeTextureKind.Cape,
                    appearance.IsSlim, operation.Token).ConfigureAwait(false);
            operation.Admit();
            return XsrResult.Success(appearance with { Skin = skin, Cape = cape });
        }
        catch (Exception error) when (Recoverable(error)) { return Reject<AccountWardrobeResolvedTextures>(error); }
    }

    private AccountWardrobeCard RequireCard(AccountWardrobeIdentity identity, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256) throw new InvalidDataException("所选材质卡片无效。");
        lock (_cardsGate)
        {
            if (_projectedIdentity != identity || !_projectedCards.TryGetValue(id, out var card))
                throw new InvalidDataException("材质卡片已失效，请刷新更衣橱。");
            return card;
        }
    }

    private AccountWardrobeResolvedTextures? ProjectedAppearance(AccountWardrobeIdentity identity)
    { lock (_cardsGate) return _projectedIdentity == identity ? _projectedCurrent : null; }
    private string? ProjectedCapeAddress(AccountWardrobeIdentity identity, string id)
    {
        lock (_cardsGate) return _projectedIdentity == identity
            ? _projectedCards.Values.FirstOrDefault(card => card.CapeId == id)?.Appearance.CapeAddress : null;
    }

    private async ValueTask<XsrResult> ApplySkinAddressAsync(AccountWardrobeCapture capture, string? address,
        bool slim, CancellationToken token)
    {
        if (capture.Profile.Kind == LaunchProfileKind.LittleSkin)
            throw new InvalidDataException("所选材质缺少 LittleSkin TID，无法直接应用。");
        Nexa.Core.Media.PngImage image;
        using (var operation = new Operation(accounts, capture, token))
        {
            if (address is null) throw new InvalidDataException("无法读取所选皮肤材质。");
            image = await _textures.ReadImageAsync(address, AccountWardrobeTextureKind.Skin, slim,
                operation.Token).ConfigureAwait(false) ?? throw new InvalidDataException("无法读取所选皮肤材质。");
            operation.Admit();
        }
        return await UploadSkinAsync(new(capture.Identity, image.Bytes.ToArray(), "pcln-skin.png", slim), token).ConfigureAwait(false);
    }

    private async ValueTask<XsrResult> ApplyLittleSkinAsync(AccountWardrobeCapture capture, long textureId,
        string title, string? address, LittleSkinTextureKind kind, bool slim, CancellationToken token)
    {
        if (textureId <= 0 || address is null || WardrobeTextureResolver.SafeAddress(address) is null)
            throw new InvalidDataException("所选 LittleSkin 材质无效。");
        var appearance = ProjectedAppearance(capture.Identity);
        using (var before = new Operation(accounts, capture, token))
        { await RememberAsync(capture, before.Token).ConfigureAwait(false); before.Admit(); }
        LittleSkinRetry retry = new();
        capture = await Authenticate(capture, token, retry).ConfigureAwait(false);
        var changed = await RetryLittleSkinAsync(capture, retry, async (admitted, providerOperation) =>
        {
            var player = await ReadPlayer(admitted.Profile, providerOperation.Token).ConfigureAwait(false);
            providerOperation.Admit();
            await littleSkin!.EnsureClosetTextureAsync(admitted.Profile.ProviderAccessToken, textureId, title, kind,
                providerOperation.Token).ConfigureAwait(false);
            providerOperation.Admit();
            await littleSkin.ApplyTextureAsync(admitted.Profile.ProviderAccessToken, player.PlayerId, textureId,
                kind, providerOperation.Token).ConfigureAwait(false);
            providerOperation.Admit();
            await VerifyLittleSkinAsync(admitted.Profile, player.PlayerId, textureId, kind, providerOperation).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        capture = changed.Capture;
        using var operation = new Operation(accounts, capture, token);
        operation.Admit();
        if (kind == LittleSkinTextureKind.Skin)
        {
            var result = accounts.CommitWardrobeSkin(capture.Identity, address, token);
            if (!result.IsSuccess) return result;
            operation.Dispose(); // The durable update retires this admitted generation.
            await RememberCommittedSkinAsync(capture, address, slim).ConfigureAwait(false);
            skins.Refresh(force: true);
        }
        else
        {
            if (appearance is not null)
                await RememberAppearanceAsync(capture.View, appearance with { CapeAddress = address, Cape = null },
                    operation.Token).ConfigureAwait(false);
            operation.Admit();
        }
        return XsrResult.Success();
    }

    private async Task VerifyLittleSkinAsync(LaunchProfile profile, long playerId, long textureId,
        LittleSkinTextureKind kind, Operation operation)
    {
        var players = await littleSkin!.GetPlayersAsync(profile.ProviderAccessToken, operation.Token).ConfigureAwait(false);
        operation.Admit();
        var matching = players.Where(player => player.PlayerId == playerId).Take(2).ToArray();
        if (matching.Length != 1 || (kind == LittleSkinTextureKind.Cape ? matching[0].CapeTextureId : matching[0].SkinTextureId) != textureId)
            throw new InvalidDataException("LittleSkin 返回的角色材质与所选材质不一致，请稍后重试。");
    }

    private async ValueTask<XsrResult> ApplyCloudSiteAsync(AccountWardrobeCapture capture, string siteId,
        long textureId, bool slim, CancellationToken token)
    {
        using var operation = new Operation(accounts, capture, token);
        await RememberAsync(capture, operation.Token).ConfigureAwait(false);
        operation.Admit();
        var result = await _cloud!.UseSiteSkinAsync(capture.View, siteId, textureId, slim, operation.Token).ConfigureAwait(false);
        operation.Admit();
        string address = CloudAddress(result);
        var saved = accounts.CommitWardrobeSkin(capture.Identity, address, token);
        if (!saved.IsSuccess) return saved;
        operation.Dispose();
        await RememberCommittedSkinAsync(capture, address, slim).ConfigureAwait(false);
        skins.Refresh(force: true);
        return XsrResult.Success();
    }

    private static string CloudAddress(AccountWardrobeCloudSkinResult result) =>
        WardrobeTextureResolver.SafeAddress(result.SkinAddress) ?? throw new InvalidDataException("N Cloud 未返回有效的皮肤地址。");

    private async Task RememberCommittedSkinAsync(AccountWardrobeCapture previous, string? address, bool slim)
    {
        var current = accounts.CaptureActiveWardrobe();
        if (!current.IsSuccess || current.Value.Identity.Index != previous.Identity.Index
            || current.Value.Identity.Uuid != previous.Identity.Uuid || current.Value.Identity.Kind != previous.Identity.Kind
            || current.Value.Identity.SelectionGeneration != previous.Identity.SelectionGeneration
            || current.Value.Identity.RosterGeneration != previous.Identity.RosterGeneration + 1) return;
        var appearance = new AccountWardrobeResolvedTextures(address, null, slim, null, null);
        if (WardrobeTextureResolver.SafeAddress(address) is null)
            appearance = await _textures.ResolveReferencesAsync(current.Value.View, CancellationToken.None).ConfigureAwait(false);
        if (!accounts.CaptureActiveWardrobe(current.Value.Identity).IsSuccess) return;
        await RememberAppearanceAsync(current.Value.View, appearance, CancellationToken.None).ConfigureAwait(false);
    }
}
