using System.Globalization;

namespace Nexa.Services.Settings;

/// <summary>One immutable game-file batch policy; shared resource admission remains authoritative.</summary>
internal sealed record MinecraftDownloadPolicy(int Concurrency = 8, bool Retry = true)
{
    internal static MinecraftDownloadPolicy Read(SettingsPolicyService? settings)
    {
        var result = settings?.Read(new());
        if (result is null || !result.IsSuccess) return new();
        var values = result.Value!.Values;
        string? count = values.Single(item => item.Key == "network.file-concurrency").Value.Value;
        bool retry = values.Single(item => item.Key == "network.file-retry").Value.Value != "false";
        return new(int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out int concurrency)
            && concurrency is >= 1 and <= 64 ? concurrency : 8, retry);
    }
}
