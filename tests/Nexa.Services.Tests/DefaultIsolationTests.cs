using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void DefaultIsolationPolicyPreservesLegacyScopeAndUnknownFacts()
    {
        string[] modes = ["none", "loaders", "non-release", "loaders-or-non-release", "all"];
        bool[][] expected = [[false, false, false, false], [false, true, false, true], [false, false, true, true], [false, true, true, true], [true, true, true, true]];
        var port = new InMemorySettingsPort(); var (settings, policy) = PolicyFixture(port);
        for (int index = 0; index < modes.Length; index++)
        {
            string mode = modes[index]; var isolation = new SettingsInstanceIsolationPolicy(mode);
            AssertEqual(expected[index][0], isolation.Isolate(false, "release"));
            AssertEqual(expected[index][1], isolation.Isolate(true, "release"));
            AssertEqual(expected[index][2], isolation.Isolate(false, "snapshot"));
            AssertEqual(expected[index][3], isolation.Isolate(true, "snapshot"));
            var legacy = new InMemorySettingsPort(); legacy.Save(new Dictionary<string, string> { ["LaunchArgumentIndieV2"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var (_, imported) = PolicyFixture(legacy);
            AssertEqual(mode, SettingsInstanceIsolationPolicy.FromSnapshot(imported.Read(new()).Value).Mode);
            AssertTrue(policy.Set(new("game.default-isolation", SettingsLayer.Global, new(SettingsOverrideMode.Custom, mode))).IsSuccess);
            AssertEqual(index, settings.GetValue<int>("LaunchArgumentIndieV2").Value);
            var (_, reopened) = PolicyFixture(port);
            AssertEqual(mode, SettingsInstanceIsolationPolicy.FromSnapshot(reopened.Read(new()).Value).Mode);
        }
        AssertFalse(policy.Set(new("game.default-isolation", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "none"), "fixture")).IsSuccess);
        AssertFalse(policy.Set(new("game.default-isolation", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "guess"))).IsSuccess);
        AssertTrue(policy.Set(new("game.default-isolation", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(4, settings.GetValue<int>("LaunchArgumentIndieV2").Value);
        foreach (string mode in new[] { "non-release", "loaders-or-non-release" })
        {
            try { new SettingsInstanceIsolationPolicy(mode).Isolate(false, null); throw new InvalidOperationException("Unknown release classified."); }
            catch (InvalidDataException) { }
        }
        AssertTrue(new SettingsInstanceIsolationPolicy("loaders-or-non-release").Isolate(true, null));
        AssertTrue(new SettingsInstanceIsolationPolicy("non-release").Isolate(false, "old_beta"));
    }

    private static async ValueTask DefaultIsolationControlsAddonDestinationAndRejectsOrphanMetadata()
    {
        string root = CreateTempDirectory(); var (_, policy) = PolicyFixture(new InMemorySettingsPort());
        try
        {
            foreach (bool isolated in new[] { false, true })
            {
                AssertTrue(policy.Set(new("game.default-isolation", SettingsLayer.Global, new(SettingsOverrideMode.Custom, isolated ? "all" : "none"))).IsSuccess);
                using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, settingsPolicy: policy);
                string name = isolated ? "isolated" : "shared";
                var artifact = new InstallDownload("Modrinth", "api.jar", new("https://example.invalid/api.jar"), null, 7);
                var result = await fixture.Install.InstallAsync(new(root, "1.20.1", InstallLoader.Fabric, "0.16.9",
                    [new(InstallLoader.FabricApi, "build", [artifact])], name));
                AssertTrue(result.IsSuccess, fixture.Entry().ErrorMessage ?? "Install failed.");
                string instance = Path.Combine(root, "versions", name);
                AssertEqual(isolated, (await new MinecraftInstanceMetadataStore().LoadAsync(instance)).InstanceIsolation);
                AssertTrue(File.Exists(Path.Combine(isolated ? instance : root, "mods", "api.jar")));
                if (!isolated) AssertFalse(Directory.Exists(Path.Combine(instance, "mods")));
            }
            string orphan = Path.Combine(root, "versions", "orphan");
            var metadata = new MinecraftInstanceMetadataStore();
            await metadata.SaveAsync(orphan, new MinecraftInstanceMetadata { InstanceIsolation = false, Description = "Preserve me" });
            using var rejected = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, settingsPolicy: policy);
            AssertFalse((await rejected.Install.InstallAsync(new(root, "1.20.1", InstanceName: "orphan"))).IsSuccess);
            AssertEqual("Preserve me", (await metadata.LoadAsync(orphan)).Description);
            AssertFalse(File.Exists(Path.Combine(orphan, "orphan.json")));
            string stage = Path.Combine(root, ".nexa-install-jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(stage, "versions", "other", "Nexa"));
            File.WriteAllText(Path.Combine(stage, "versions", "other", "Nexa", "InstanceMetadata.json"), "{}");
            try
            {
                await InstallPublicationJournal.PrepareAsync(root, stage, "selected", ["versions/other/Nexa/InstanceMetadata.json"], new Dictionary<string, string>(), default);
                throw new InvalidOperationException("Unrelated instance metadata accepted.");
            }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
