namespace Nexa.Services.Accounts;

public static class AccountVerificationContract
{
    public static bool IsVerificationUri(AccountLoginProvider provider, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return false;
        return provider switch
        {
            AccountLoginProvider.Microsoft => uri.Host is "microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com",
            AccountLoginProvider.LittleSkin => uri.Host is "littleskin.cn" or "open.littleskin.cn" or "www.littleskin.cn",
            _ => false,
        };
    }

}
