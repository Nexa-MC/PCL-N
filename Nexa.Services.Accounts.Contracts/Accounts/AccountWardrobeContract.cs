using Nexa.Core.Media;
using Nexa.Xsr;

namespace Nexa.Services.Accounts;

/// <summary>Opaque admission stamp for one selected account; it contains no credentials.</summary>
public sealed record AccountWardrobeIdentity(int Index, string Uuid, LaunchProfileKind Kind,
    long RosterGeneration, long SelectionGeneration);
public sealed record AccountWardrobeCape(string Id, string Name, string TextureAddress, bool IsActive);
public sealed record AccountWardrobeSnapshot(AccountWardrobeIdentity Identity, LaunchProfileView Profile,
    bool CanUploadSkin, bool CanChooseCape, string? UnavailableReason, IReadOnlyList<AccountWardrobeCape> Capes);
public sealed record AccountWardrobeQuery;
public sealed record AccountWardrobeSkinQuery(AccountWardrobeIdentity Identity, string Path, bool IsSlim);
public sealed record AccountWardrobeSkinPreview(PngImage Image, bool IsSlim, string FileName);
public sealed record AccountWardrobeUploadSkinCommand(AccountWardrobeIdentity Identity, byte[] PngBytes,
    string FileName, bool IsSlim);
/// <summary>A null cape ID removes the current cape.</summary>
public sealed record AccountWardrobeSetCapeCommand(AccountWardrobeIdentity Identity, string? CapeId);

public static class AccountWardrobeContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("accounts.wardrobe.read");
    public static readonly XsrSemanticId ValidateSkin = XsrSemanticId.Parse("accounts.wardrobe.validate_skin");
    public static readonly XsrSemanticId UploadSkin = XsrSemanticId.Parse("accounts.wardrobe.upload_skin");
    public static readonly XsrSemanticId SetCape = XsrSemanticId.Parse("accounts.wardrobe.set_cape");
}
