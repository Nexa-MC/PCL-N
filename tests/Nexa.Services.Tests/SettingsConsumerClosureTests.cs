using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void BandwidthPolicyPreservesLegacySliderAndExactNewValues()
    {
        var port = new InMemorySettingsPort();
        foreach (var (slider, expected) in new[] { (0, "102"), (14, "1536"), (15, "2048"), (31, "10240"), (32, "11264"), (41, "20480"), (42, "0") })
        {
            port.Save(new Dictionary<string, string> { ["ToolDownloadSpeed"] = slider.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var (_, legacy) = PolicyFixture(port);
            AssertEqual(expected, Effective(legacy, "network.bandwidth-kib").Value.Value);
        }
        var (settings, policy) = PolicyFixture(port);
        AssertTrue(policy.Set(new("network.bandwidth-kib", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "137"))).IsSuccess);
        var (_, restarted) = PolicyFixture(port);
        AssertEqual("137", Effective(restarted, "network.bandwidth-kib").Value.Value);
        foreach (string invalid in new[] { "-1", "1048577", "1.5" })
            AssertFalse(policy.Set(new("network.bandwidth-kib", SettingsLayer.Global, new(SettingsOverrideMode.Custom, invalid))).IsSuccess);
        AssertFalse(policy.Set(new("network.bandwidth-kib", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "100"), Path.GetFullPath("bandwidth-instance"))).IsSuccess);
        AssertTrue(policy.Set(new("network.bandwidth-kib", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(42, settings.GetValue<int>("ToolDownloadSpeed").Value);
        AssertEqual("0", Effective(policy, "network.bandwidth-kib").Value.Value);
    }

    private static void ConsumerCatalogRequiresRealContractsAndKeepsFutureFeaturesReserved()
    {
        var catalog = SettingsCatalog.Read(new(true));
        foreach (string key in new[] { "appearance.theme-mode", "appearance.accent", "game.wrapper", "game.pre-launch", "game.pre-launch-wait", "network.proxy-mode", "network.proxy-address", "network.proxy-user", "network.proxy-password", "network.doh", "network.ip-stack", "network.bandwidth-kib", "diagnostics.disk-log-days" })
        {
            var entries = catalog.Entries.Where(entry => entry.SettingKey == key).ToArray();
            AssertTrue(entries.Length > 0);
            AssertTrue(entries.All(entry => entry.Availability == SettingsCapabilityAvailability.Available && entry.Definition is not null));
        }
        foreach (string id in new[] { "global.storage.launcher-data", "global.storage.move-launcher-data", "global.storage.finished-tasks", "global.storage.5ac61e098807", "global.storage.1f8794991a2c" })
            AssertEqual(SettingsCapabilityAvailability.Available, catalog.Entries.Single(entry => entry.Id == id).Availability);
        foreach (string id in new[] { "global.general.13a5ea9202aa", "global.appearance.81b6d3198ecc" })
            AssertEqual(SettingsCapabilityAvailability.Available, catalog.Entries.Single(entry => entry.Id == id).Availability);
        foreach (string id in new[] { "global.appearance.414d92ec0335", "global.appearance.5362145d1e89" })
            AssertEqual(SettingsCapabilityAvailability.PlatformUnsupported, catalog.Entries.Single(entry => entry.Id == id).Availability);
    }
}
