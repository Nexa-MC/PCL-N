


using Nexa.Core.Media;




namespace Nexa.Services.Accounts;

public sealed record AccountSkinSnapshot(string ProfileKey, PngImage? Image);
public sealed record AccountRefreshSkinsCommand;
