using System.Security.Cryptography;
using System.Text;
using Nexa.Xsr;
namespace Nexa.Services.Accounts;

public static class AccountSkinContract
{
    public static readonly XsrSemanticId SkinsKey = XsrSemanticId.Parse("accounts.skins");
    public static readonly XsrSemanticId RefreshRoute = XsrSemanticId.Parse("accounts.skins.refresh");
    public static string ProfileKey(LaunchProfileView profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{profile.Kind}\n{profile.Uuid}\n{profile.AuthServer}\n{profile.SkinAddress}\n{profile.Username}")));
}
