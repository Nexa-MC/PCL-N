// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 Nexa contributors.
// Licensed under the Apache License, Version 2.0.









namespace Nexa.Services.Accounts;

/// <summary>
/// LittleSkin OAuth client configuration. The client secret is required for
/// authorization-code exchange only; the device flow uses the public client id.
/// </summary>
public sealed record LittleSkinOAuthConfiguration(
    string ClientId,
    string ClientSecret,
    Uri RedirectUri);

public sealed record LittleSkinAuthorizationRequest(
    Uri AuthorizationUri,
    string State);

/// <summary>Device authorization grant (RFC 8628) pair from open.littleskin.cn.</summary>
public sealed record LittleSkinDeviceCodeInfo(
    string UserCode,
    string DeviceCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

public sealed record LittleSkinOAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? IdToken = null);

public sealed record LittleSkinProfile(
    string Username,
    string Uuid);

public sealed record LittleSkinMinecraftSession(
    string Username,
    string Uuid,
    string AccessToken,
    string ClientToken);

public sealed record LittleSkinPlayer(
    long PlayerId,
    string Username,
    long SkinTextureId,
    long CapeTextureId);

public enum LittleSkinTextureKind
{
    Skin,
    Cape
}

public sealed record LittleSkinClosetItem(
    long TextureId,
    string Name,
    string Model,
    string TextureAddress,
    LittleSkinTextureKind Kind);

public sealed record LittleSkinTextureUploadResult(
    string ProfileUuid,
    LittleSkinTextureKind Kind,
    bool IsSlim);

public interface ILittleSkinOAuthService
{
    LittleSkinAuthorizationRequest CreateAuthorizationRequest(
        LittleSkinOAuthConfiguration configuration,
        string state);

    Task<LittleSkinDeviceCodeInfo> RequestDeviceCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<LittleSkinOAuthTokens> WaitForDeviceAuthorizationAsync(
        LittleSkinOAuthConfiguration configuration,
        LittleSkinDeviceCodeInfo deviceCode,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<LittleSkinOAuthTokens> ExchangeAuthorizationCodeAsync(
        LittleSkinOAuthConfiguration configuration,
        string code,
        CancellationToken cancellationToken = default);

    Task<LittleSkinOAuthTokens> RefreshOAuthTokenAsync(
        LittleSkinOAuthConfiguration configuration,
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinProfile>> GetProfilesAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(
        string accessToken,
        string uuid,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinPlayer>> GetPlayersAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LittleSkinClosetItem>> GetClosetItemsAsync(
        string accessToken,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);

    Task ApplyTextureAsync(
        string accessToken,
        long playerId,
        long textureId,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);

    Task EnsureClosetTextureAsync(
        string accessToken,
        long textureId,
        string name,
        LittleSkinTextureKind kind,
        CancellationToken cancellationToken = default);

    Task<LittleSkinTextureUploadResult> UploadMinecraftTextureAsync(
        string minecraftAccessToken,
        string profileUuid,
        byte[] pngBytes,
        string fileName,
        bool isSlim,
        CancellationToken cancellationToken = default);
}
