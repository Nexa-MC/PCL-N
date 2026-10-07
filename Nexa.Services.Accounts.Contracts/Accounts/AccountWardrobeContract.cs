using Nexa.Core.Media;
using Nexa.Xsr;

namespace Nexa.Services.Accounts;

/// <summary>Opaque admission stamp for one selected account; it contains no credentials.</summary>
public sealed record AccountWardrobeIdentity(int Index, string Uuid, LaunchProfileKind Kind,
    long RosterGeneration, long SelectionGeneration);
public sealed record AccountWardrobeCape(string Id, string Name, string TextureAddress, bool IsActive);
public enum AccountWardrobeCapeState { Loading, Loaded, LoadFailed, Unsupported }
public sealed record AccountWardrobeCard(string Id, string Title, string Source,
    AccountWardrobeResolvedTextures Appearance, bool CanApply, long? TextureId = null, string? CapeId = null)
{
    public AccountWardrobeTextureKind Kind { get; init; }
    public bool IsActive { get; init; }
}
public sealed record AccountWardrobeSnapshot(AccountWardrobeIdentity Identity, LaunchProfileView Profile,
    bool CanUploadSkin, bool CanChooseCape, string? UnavailableReason, IReadOnlyList<AccountWardrobeCape> Capes)
{
    public AccountWardrobeResolvedTextures? Current { get; init; }
    public IReadOnlyList<AccountWardrobeCard> Skins { get; init; } = [];
    public IReadOnlyList<AccountWardrobeCard> CapeCards { get; init; } = [];
    public AccountWardrobeCapeState CapeState { get; init; } = AccountWardrobeCapeState.Loaded;
    public string? ManageUri { get; init; }
    public string? ProviderStatus { get; init; }
}
public sealed record AccountWardrobeQuery;
public sealed record AccountWardrobeSkinQuery(AccountWardrobeIdentity Identity, string Path, bool IsSlim);
public sealed record AccountWardrobeSkinPreview(PngImage Image, bool IsSlim, string FileName);
public sealed record AccountWardrobeUploadSkinCommand(AccountWardrobeIdentity Identity, byte[] PngBytes,
    string FileName, bool IsSlim);
/// <summary>A null cape ID removes the current cape.</summary>
public sealed record AccountWardrobeSetCapeCommand(AccountWardrobeIdentity Identity, string? CapeId);
/// <summary>Applies only a card previously projected for this admitted account.</summary>
public sealed record AccountWardrobeApplyCardCommand(AccountWardrobeIdentity Identity, string CardId);
/// <summary>The service resolves this fixed-site texture ID; callers cannot submit a download URL.</summary>
public sealed record AccountWardrobeApplyPublicCommand(AccountWardrobeIdentity Identity, string SiteId,
    long TextureId, AccountWardrobeTextureKind Kind);
public sealed record AccountWardrobeTextureQuery(AccountWardrobeIdentity Identity, string? CardId = null,
    string? SiteId = null, long TextureId = 0, AccountWardrobeTextureKind Kind = AccountWardrobeTextureKind.Skin);
public sealed record AccountWardrobeCloudSkinResult(string SkinAddress, string SourceKind, string? Sha1 = null);
/// <summary>Optional cloud capability. It receives account facts without game or provider credentials.</summary>
public interface INCloudWardrobePort
{
    bool CanManage(LaunchProfileView profile);
    Task<AccountWardrobeCloudSkinResult> UploadSkinAsync(LaunchProfileView profile, byte[] pngBytes,
        bool isSlim, CancellationToken cancellationToken);
    Task<AccountWardrobeCloudSkinResult> UseSiteSkinAsync(LaunchProfileView profile, string siteId,
        long textureId, bool isSlim, CancellationToken cancellationToken);
}

public static class AccountWardrobeContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("accounts.wardrobe.read");
    public static readonly XsrSemanticId ValidateSkin = XsrSemanticId.Parse("accounts.wardrobe.validate_skin");
    public static readonly XsrSemanticId UploadSkin = XsrSemanticId.Parse("accounts.wardrobe.upload_skin");
    public static readonly XsrSemanticId SetCape = XsrSemanticId.Parse("accounts.wardrobe.set_cape");
    public static readonly XsrSemanticId ApplyCard = XsrSemanticId.Parse("accounts.wardrobe.apply_card");
    public static readonly XsrSemanticId ApplyPublic = XsrSemanticId.Parse("accounts.wardrobe.apply_public");
    public static readonly XsrSemanticId ReadTexture = XsrSemanticId.Parse("accounts.wardrobe.read_texture");
}
