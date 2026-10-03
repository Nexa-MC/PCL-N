using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void UpdatePreferencesFollowBuildAndPersistExplicitChoices()
    {
        var port = new InMemorySettingsPort(); var (settings, service) = PolicyFixture(port);
        var original = service.Read(new()).Value!;
        AssertEqual("stable", SettingsUpdatePolicy.FromSnapshot(original, "stable").Channel);
        AssertEqual("beta", SettingsUpdatePolicy.FromSnapshot(original, "beta").Channel);
        AssertEqual("alpha", SettingsUpdatePolicy.FromSnapshot(original, "ci").Channel);
        AssertTrue(SettingsUpdatePolicy.FromSnapshot(original, "alpha").AutomaticCheck);
        AssertTrue(settings.SetRawValues(new Dictionary<string, string> { ["SystemUpdateMode"] = "3" }).IsSuccess);
        AssertFalse(SettingsUpdatePolicy.FromSnapshot(service.Read(new()).Value!, "alpha").AutomaticCheck);
        AssertTrue(service.Set(new("updates.auto-check", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "true"))).IsSuccess);
        AssertTrue(service.Set(new("updates.channel", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "beta"))).IsSuccess);
        var (_, reopened) = PolicyFixture(port);
        var persisted = SettingsUpdatePolicy.FromSnapshot(reopened.Read(new()).Value!, "stable");
        AssertEqual("beta", persisted.Channel); AssertTrue(persisted.AutomaticCheck);
        AssertFalse(service.Set(new("updates.channel", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "stable"), Path.GetFullPath("update-scope"))).IsSuccess);
        AssertTrue(SettingsCatalog.Entries.Single(item => item.SettingKey == "updates.auto-check").Availability == SettingsCapabilityAvailability.Available);
    }
}
