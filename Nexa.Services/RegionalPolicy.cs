using System.Globalization;

namespace Nexa.Services;

/// <summary>Product country policy, independent of the chosen display language.</summary>
public sealed record RegionalPolicy(string CountryCode)
{
    public bool IsMainlandChina => string.Equals(CountryCode, "CN", StringComparison.OrdinalIgnoreCase);
    public bool RequireMinecraftOwnership => !IsMainlandChina;
    public const string PurchaseReminder = "请支持并购买正版 Minecraft；其他档案仅用于你有权访问的游戏与服务器。";
    public static RegionalPolicy Current { get; } = Resolve();

    public static RegionalPolicy Resolve(string? country = null)
    {
        country ??= Environment.GetEnvironmentVariable("NEXA_COUNTRY");
        if (string.IsNullOrWhiteSpace(country))
        {
            try { country = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (ArgumentException) { country = ""; }
        }
        string normalized = country.Trim().ToUpperInvariant();
        return new(normalized.Length == 2 && normalized.All(char.IsAsciiLetter) ? normalized : "");
    }
}
