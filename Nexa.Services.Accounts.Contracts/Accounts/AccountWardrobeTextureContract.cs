using Nexa.Core.Media;

namespace Nexa.Services.Accounts;

/// <summary>The two public texture kinds used by the wardrobe and its address history.</summary>
public enum AccountWardrobeTextureKind
{
    Skin,
    Cape,
}

/// <summary>Public texture locations and immutable, validated encoded previews; no account credentials.</summary>
public sealed record AccountWardrobeResolvedTextures(string? SkinAddress, string? CapeAddress, bool IsSlim,
    PngImage? Skin, PngImage? Cape);

/// <summary>Compatible with the dev wardrobe's optional Appearance/history.json entry schema.</summary>
public sealed record AccountWardrobeHistoryEntry(string ProfileKey, string DisplayName,
    AccountWardrobeTextureKind Kind, string Address, bool IsSlim, DateTimeOffset LastUsedUtc);
