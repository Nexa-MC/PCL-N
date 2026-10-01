




namespace Nexa.Services.Accounts;

/// <summary>The outcome of a Microsoft-profile skin upload.</summary>
public sealed record MinecraftSkinUploadResult(string? SkinAddress);

/// <summary>One cape owned by the authenticated Microsoft profile.</summary>
public sealed record MinecraftOwnedCape(
    string Id,
    string Alias,
    string TextureAddress,
    bool IsActive);
