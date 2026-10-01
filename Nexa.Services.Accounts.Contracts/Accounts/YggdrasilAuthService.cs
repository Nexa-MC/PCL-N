




namespace Nexa.Services.Accounts;

/// <summary>One Yggdrasil authenticate/refresh request.</summary>
public sealed record YggdrasilAuthLoginRequest(
    string Server,
    string Username,
    string Password,
    string? ClientToken = null);

/// <summary>One Yggdrasil authenticate/refresh outcome, credentials included.</summary>
public sealed record YggdrasilAuthLoginResult(
    string Username,
    string Uuid,
    string AccessToken,
    string AuthServer,
    string AuthServerDisplayName,
    string ClientToken = "",
    string RefreshToken = "");
