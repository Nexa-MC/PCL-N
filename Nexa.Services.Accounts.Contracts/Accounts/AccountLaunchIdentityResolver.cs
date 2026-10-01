
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Accounts;

/// <summary>
/// Resolves the launch identity for one persisted profile. Account-provider specifics live
/// here — offline derivation, Microsoft token refresh, and LittleSkin game sessions — so the launch
/// coordinator never grows provider-specific branches.
/// </summary>
public interface IAccountLaunchIdentityResolver
{
    ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveAsync(
        int accountIndex,
        LaunchProfile profile,
        CancellationToken cancellationToken = default);
}
