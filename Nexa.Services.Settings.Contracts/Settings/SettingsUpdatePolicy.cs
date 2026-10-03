namespace Nexa.Services.Settings;

/// <summary>Discovery preferences, separate from the running build's immutable identity.</summary>
public sealed record SettingsUpdatePolicy(string Channel, bool AutomaticCheck)
{
    public static SettingsUpdatePolicy FromSnapshot(SettingsEffectiveSnapshot snapshot, string buildChannel)
    {
        string? requested = snapshot.Values.FirstOrDefault(item => item.Key == "updates.channel" && item.ValidationError is null)?.Value.Value;
        string channel = requested is "stable" or "alpha" or "beta" or "ci" ? requested : buildChannel;
        if (channel == "ci") channel = "alpha";
        if (channel is not ("stable" or "alpha" or "beta")) channel = "stable";
        bool automatic = snapshot.Values.FirstOrDefault(item => item.Key == "updates.auto-check" && item.ValidationError is null)?.Value.Value == "true";
        return new(channel, automatic);
    }
}
