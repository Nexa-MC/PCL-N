using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Downloads;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ScopedRepairSettingControlsActualPreparation()
    {
        string root = CreateTempDirectory();
        try
        {
            string directory = CreateVersionDirectory(root, "1.20.1", new JsonObject
            {
                ["id"] = "1.20.1",
                ["mainClass"] = "example.Main",
                ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
                ["libraries"] = new JsonArray(new JsonObject
                {
                    ["name"] = "example:missing:1.0",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        { ["path"] = "example/missing/1.0/missing-1.0.jar", ["url"] = "https://example.invalid/missing.jar", ["size"] = 8, ["sha1"] = Sha1Hex("EXPECTED") }
                    }
                })
            });
            var host = FoundationComposer.Compose(new InMemorySettingsPort(), LauncherDefaults.CreateSchema(),
                new LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            AssertTrue(host.Accounts.AddProfile(new LaunchProfile { Username = "Player", Kind = LaunchProfileKind.Offline }).IsSuccess);
            string java = Path.Combine(root, "java.exe"); await File.WriteAllBytesAsync(java, [0]);
            var candidate = new JavaRuntimeCandidate(new JavaInstallation(root, java, null, new Version(17, 0),
                JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, false));
            int requests = 0;
            using var completion = new MinecraftLaunchFileCompletion(host.Downloads, connectionFactory: _ =>
            { Interlocked.Increment(ref requests); return new ServingConnection("BAD"u8.ToArray()); }, settingsPolicy: host.SettingsPolicy);
            var coordinator = new MinecraftLaunchCoordinator(root, Path.Combine(root, "runtime"), new MinecraftInstanceDiscovery(),
                host.Accounts, host.Settings, new JavaSelectionService(new InMemoryJavaLocator([candidate])), new NeverJavaInstaller(),
                new MinecraftLaunchExecutor(new MinecraftProcessService(hostStore: host.StateStore)),
                new MinecraftLaunchPlatform(MinecraftLibraryOperatingSystem.Win32, "10.0", true, false),
                fileCompletion: completion, settingsPolicy: host.SettingsPolicy);
            AssertTrue(host.SettingsPolicy.Set(new("network.file-retry", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
            AssertTrue(host.SettingsPolicy.Set(new("game.auto-repair", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
            AssertTrue((await coordinator.PrepareAsync("1.20.1", 0)).IsSuccess); AssertEqual(0, requests);
            AssertTrue(host.SettingsPolicy.Set(new("game.auto-repair", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "true"), directory)).IsSuccess);
            AssertFalse((await coordinator.PrepareAsync("1.20.1", 0)).IsSuccess); AssertTrue(requests > 0);
            int repairRequests = requests;
            AssertTrue(host.SettingsPolicy.Set(new("game.auto-repair", SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), directory)).IsSuccess);
            AssertTrue((await coordinator.PrepareAsync("1.20.1", 0)).IsSuccess); AssertEqual(repairRequests, requests);
            foreach (string invalid in new[] { "https://example.invalid", "has space", "line\nbreak", new string('a', 513) })
                AssertFalse(host.SettingsPolicy.Set(new("game.server", SettingsLayer.Global, new(SettingsOverrideMode.Custom, invalid))).IsSuccess);
            AssertTrue(host.SettingsPolicy.Set(new("game.server", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "[::1]:25565"))).IsSuccess);
            AssertEqual("[::1]:25565", (await coordinator.PrepareAsync("1.20.1", 0)).Value.Request.Server);
        }
        finally { Directory.Delete(root, true); }
    }
}
