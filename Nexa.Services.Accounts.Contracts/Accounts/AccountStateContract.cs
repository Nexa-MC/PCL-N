using Nexa.Xsr;

namespace Nexa.Services.Accounts;

public static class AccountStateContract
{
    public static readonly XsrSemanticId ProfilesKey = XsrSemanticId.Parse("accounts.profiles");
    public static readonly XsrSemanticId SelectedKey = XsrSemanticId.Parse("accounts.selected");
}
