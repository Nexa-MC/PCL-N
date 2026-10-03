using Nexa.Services;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void GameSourceSettingsKeepAuthorityAndRegionBoundaries()
    {
        const string original = "https://piston-data.mojang.com/file.jar";
        string[] sources = [original, "https://bmclapi2.bangbang93.com/file.jar"];
        AssertEqual(original, new MinecraftDownloadPolicy().SelectSources(original, sources, new("CN"))[0]);
        AssertEqual(sources[1], new MinecraftDownloadPolicy(Source: "mirrors-first").SelectSources(original, sources, new("CN"))[0]);
        AssertEqual(original, new MinecraftDownloadPolicy(Source: "official-only").SelectSources(original, sources, new("CN")).Single());
        foreach (string country in new[] { "US", "TW", "HK", "MO", "" })
            AssertEqual(original, new MinecraftDownloadPolicy(Source: "mirrors-first").SelectSources(original, sources, new(country)).Single());
        string[] unhashed = MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources(original, true, null);
        AssertEqual(original, new MinecraftDownloadPolicy(Source: "mirrors-first").SelectSources(original, unhashed, new("CN")).Single());
        var port = new InMemorySettingsPort();
        foreach (var pair in new[] { ("0", "mirrors-first"), ("1", "official-first"), ("2", "official-only") })
        {
            port.Save(new Dictionary<string, string> { ["ToolDownloadSource"] = pair.Item1 });
            var (_, policy) = PolicyFixture(port);
            AssertEqual(pair.Item2, MinecraftDownloadPolicy.Read(policy).Source);
            AssertTrue(policy.Set(new("network.game-source", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "official-only"))).IsSuccess);
            var (_, reopened) = PolicyFixture(port);
            AssertEqual("official-only", MinecraftDownloadPolicy.Read(reopened).Source);
            AssertFalse(policy.Set(new("network.game-source", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "unknown-source"))).IsSuccess);
            AssertTrue(policy.Set(new("network.game-source", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
            AssertEqual("official-first", MinecraftDownloadPolicy.Read(policy).Source);
        }
    }

    private static async ValueTask GameSourceSettingsReachInstallAndLaunchTransfers()
    {
        foreach (string sourcePolicy in new[] { "official-first", "mirrors-first", "official-only" })
        {
            var (_, policy) = PolicyFixture();
            AssertTrue(policy.Set(new("network.game-source", SettingsLayer.Global, new(SettingsOverrideMode.Custom, sourcePolicy))).IsSuccess);
            var requested = new List<string>();
            IDownloadConnection Connect(string source)
            {
                lock (requested) requested.Add(source);
                if (source.EndsWith("/5.json", StringComparison.Ordinal))
                    return new ServingConnection(System.Text.Encoding.UTF8.GetBytes(AssetIndexJson().ToJsonString()));
                return new ServingConnection(PayloadFor(source));
            }
            var metadata = new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
            using var install = new InstallFixture(metadata, settingsPolicy: policy, connectionFactory: Connect);
            string root = CreateTempDirectory();
            try
            {
                AssertTrue((await install.Install.InstallAsync(new(root, "1.20.1", null, null))).IsSuccess);
                AssertSources();
                requested.Clear();
                var instance = new MinecraftInstanceDescriptor("1.20.1", Path.Combine(root, "versions", "1.20.1"), "1.20.1",
                    new MinecraftVersionDescriptor("1.20.1", "1.20.1", Path.Combine(root, "versions", "1.20.1", "1.20.1.json"), null, null, null, null,
                        new MinecraftVersionClassification("1.20.1", "release", MinecraftVersionCategory.Release, null)), new MinecraftInstanceMetadata());
                File.Delete(Path.Combine(instance.DirectoryPath, "1.20.1.jar"));
                using var fixture = new CompletionFixture();
                using var completion = new MinecraftLaunchFileCompletion(fixture.Downloads, connectionFactory: Connect, settingsPolicy: policy);
                await completion.CompleteAsync(root, instance, new(VanillaJson(), []), new(MinecraftLibraryOperatingSystem.Win32, "10.0", true, false),
                    "offline", null, CancellationToken.None);
                AssertSources();
                void AssertSources()
                {
                    AssertTrue(requested.Count > 0);
                    bool mirror = sourcePolicy == "mirrors-first" && RegionalPolicy.Current.IsMainlandChina;
                    AssertTrue(requested.All(url => url.Contains("bmclapi2", StringComparison.Ordinal) == mirror));
                }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
