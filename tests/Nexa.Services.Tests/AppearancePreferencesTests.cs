using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void AppearancePreferencesRetainLegacyModesRestartAndDurableFailures()
    {
        var port = new PolicyFailingPort();
        var (settings, policy) = PolicyFixture(port);
        AssertEqual("2", Effective(policy, "appearance.theme-mode").Value.Value);
        AssertEqual("blue", Effective(policy, "appearance.accent").Value.Value);
        foreach (string mode in new[] { "0", "1", "2" })
        {
            var legacy = new InMemorySettingsPort();
            legacy.Save(new Dictionary<string, string> { ["UiDarkMode"] = mode });
            AssertEqual(mode, Effective(PolicyFixture(legacy).Policy, "appearance.theme-mode").Value.Value);
            AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, mode))).IsSuccess);
            AssertEqual(int.Parse(mode, System.Globalization.CultureInfo.InvariantCulture), settings.GetValue<int>("UiDarkMode").Value);
            AssertEqual(mode, Effective(PolicyFixture(port).Policy, "appearance.theme-mode").Value.Value);
        }
        foreach (string accent in new[] { "blue", "purple", "green", "orange" })
        {
            AssertTrue(policy.Set(new("appearance.accent", SettingsLayer.Global, new(SettingsOverrideMode.Custom, accent))).IsSuccess);
            AssertEqual(accent, settings.GetValue<string>("UiAccentColor").Value);
            AssertEqual(accent, Effective(PolicyFixture(port).Policy, "appearance.accent").Value.Value);
        }
        long revision = settings.Revision;
        AssertFalse(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "3"))).IsSuccess);
        AssertFalse(policy.Set(new("appearance.accent", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "custom"))).IsSuccess);
        AssertFalse(policy.Set(new("appearance.theme-mode", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1"), Path.GetFullPath("appearance-instance"))).IsSuccess);
        AssertEqual(revision, settings.Revision);
        port.Fail = true;
        AssertFalse(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
        AssertFalse(policy.Set(new("appearance.accent", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "blue"))).IsSuccess);
        AssertEqual(revision, settings.Revision);
        AssertEqual("2", Effective(policy, "appearance.theme-mode").Value.Value);
        AssertEqual("orange", Effective(policy, "appearance.accent").Value.Value);
        AssertEqual("orange", Effective(PolicyFixture(port).Policy, "appearance.accent").Value.Value);
        port.Fail = false;
        AssertTrue(policy.Set(new("appearance.accent", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual("blue", Effective(policy, "appearance.accent").Value.Value);
        foreach (string id in new[] { "global.appearance.d7137f424ce4", "global.appearance.dc9cc5ed3077" })
            AssertEqual(SettingsCapabilityAvailability.Available, SettingsCatalog.Entries.Single(item => item.Id == id).Availability);
    }
}
