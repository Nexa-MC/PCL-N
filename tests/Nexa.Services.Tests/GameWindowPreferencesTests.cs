using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void GameWindowPreferencesCaptureScopesAndLegacyValues()
    {
        var port = new InMemorySettingsPort(); var (settings, policy) = PolicyFixture(port);
        string root = CreateTempDirectory(), instance = Path.Combine(root, "versions", "window-fixture");
        try
        {
            Directory.CreateDirectory(instance); File.WriteAllBytes(Path.Combine(instance, "window-fixture.jar"), [1]);
            var original = new MinecraftLaunchRequest
            {
                VersionJson = new JsonObject { ["id"] = "window-fixture", ["mainClass"] = "fixture.Main", ["minecraftArguments"] = "--username ${auth_player_name}" },
                VersionId = "window-fixture",
                InstanceDirectory = instance,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
                WindowTitle = "Metadata title"
            };
            AssertTrue(policy.Set(new("game.title", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "Global title"))).IsSuccess);
            AssertEqual("Metadata title", MinecraftLaunchCoordinator.ApplySettings(original, policy.Read(new(instance)).Value!).WindowTitle);
            AssertEqual("Global title", MinecraftLaunchCoordinator.ApplySettings(original with { WindowTitle = "" }, policy.Read(new(instance)).Value!).WindowTitle);
            AssertTrue(policy.Set(new("game.title", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, ""), instance)).IsSuccess);
            AssertEqual("", MinecraftLaunchCoordinator.ApplySettings(original, policy.Read(new(instance)).Value!).WindowTitle);
            AssertTrue(policy.Set(new("game.title", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "Instance title"), instance)).IsSuccess);
            AssertTrue(policy.Set(new("game.launcher-visibility", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "hide"), instance)).IsSuccess);
            var (_, reopened) = PolicyFixture(port);
            var request = MinecraftLaunchCoordinator.ApplySettings(original, reopened.Read(new(instance)).Value!);
            var plan = MinecraftLaunchPlanner.CreatePlan(request);
            AssertEqual("Instance title", plan.WindowTitle); AssertEqual(MinecraftLauncherVisibility.Hide, plan.LauncherVisibility);
            AssertTrue(policy.Set(new("game.title", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "Changed"), instance)).IsSuccess);
            AssertEqual("Instance title", plan.WindowTitle);
            AssertFalse(policy.Set(new("game.title", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "bad\nvalue"))).IsSuccess);
            AssertFalse(policy.Set(new("game.title", SettingsLayer.Global, new(SettingsOverrideMode.Custom, new string('x', 513)))).IsSuccess);
            AssertFalse(policy.Set(new("game.launcher-visibility", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "quit-now"))).IsSuccess);
            foreach (var (legacy, expected) in new[] { ("0", "hide-and-close"), ("2", "hide-and-close"), ("3", "hide"), ("4", "minimize"), ("5", "keep") })
            {
                var legacyPort = new InMemorySettingsPort(); legacyPort.Save(new Dictionary<string, string> { ["LaunchArgumentVisible"] = legacy });
                var (_, imported) = PolicyFixture(legacyPort); AssertEqual(expected, Effective(imported, "game.launcher-visibility").Value.Value);
                AssertTrue(policy.Set(new("game.launcher-visibility", SettingsLayer.Global, new(SettingsOverrideMode.Custom, expected))).IsSuccess);
                AssertEqual(legacy == "0" ? 2 : int.Parse(legacy, System.Globalization.CultureInfo.InvariantCulture), settings.GetValue<int>("LaunchArgumentVisible").Value);
            }
            AssertTrue(policy.Set(new("game.launcher-visibility", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
            AssertEqual(5, settings.GetValue<int>("LaunchArgumentVisible").Value);
            AssertEqual(OperatingSystem.IsWindows() ? SettingsCapabilityAvailability.Available : SettingsCapabilityAvailability.PlatformUnsupported,
                SettingsCatalog.Entries.First(entry => entry.SettingKey == "game.title").Availability);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask GameWindowPreferencesReachTrackedProcess()
    {
        string root = CreateTempDirectory();
        try
        {
            await using var processes = new MinecraftProcessService(new LongLivedProcessPort());
            var plan = new MinecraftLaunchPlan("java", root, ["fixture.Main"], [], [], new(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []))
            { WindowTitle = "Captured", LauncherVisibility = MinecraftLauncherVisibility.HideAndClose };
            var session = await processes.StartAsync(plan, "window-fixture");
            AssertEqual("Captured", session.Snapshot.WindowTitle); AssertEqual(MinecraftLauncherVisibility.HideAndClose, session.Snapshot.LauncherVisibility);
            session.Cancel(); await session.WaitForExitAsync();
            AssertEqual("Captured", session.Snapshot.WindowTitle);
        }
        finally { Directory.Delete(root, true); }
    }
}
