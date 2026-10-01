





namespace Nexa.Services.Accounts;

/// <summary>One Microsoft device-code login session offered to the user.</summary>
public sealed record MicrosoftDeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    string Message,
    TimeSpan ExpiresIn,
    TimeSpan PollInterval);

/// <summary>The full Microsoft → Minecraft login outcome, credentials included.</summary>
public sealed record MicrosoftMinecraftLoginResult(
    string Username,
    string Uuid,
    string AccessToken,
    string RefreshToken,
    string? SkinAddress,
    bool OwnsMinecraft);

/// <summary>
/// The Microsoft device-code login chain: Microsoft OAuth → Xbox Live → XSTS → Minecraft
/// services → profile and ownership.
/// </summary>
public interface IMicrosoftMinecraftAuthService
{
    Task<MicrosoftDeviceCodeInfo> RequestDeviceCodeAsync(
        string clientId,
        CancellationToken cancellationToken = default);

    Task<MicrosoftMinecraftLoginResult> CompleteDeviceLoginAsync(
        string clientId,
        MicrosoftDeviceCodeInfo deviceCode,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MicrosoftMinecraftLoginResult> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default);
}
