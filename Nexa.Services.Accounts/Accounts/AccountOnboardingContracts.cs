


namespace Nexa.Services.Accounts;

public sealed record AccountOnboardingOptions(string MicrosoftClientId, LittleSkinOAuthConfiguration? LittleSkin)
{
    public static AccountOnboardingOptions FromEnvironment()
    {
        LittleSkinOAuthConfiguration? littleSkin = null;
        try { littleSkin = LittleSkinOAuthService.ResolveConfiguration(); }
        catch (InvalidOperationException) { /* Configuration errors belong to the login action, not app startup. */ }
        return new(MicrosoftMinecraftAuthService.ResolveClientId(), littleSkin);
    }
}
