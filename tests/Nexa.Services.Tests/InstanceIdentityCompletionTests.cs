using System.Text.Json.Nodes;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceIdentityPersistsRejectsStaleAndProtectsLockedAuthentication()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "identity", new JsonObject { ["id"] = "identity", ["mainClass"] = "fixture.Main", ["_minecraftVersion"] = "1.20.1" });
            var before = await InstanceIdentityService.ReadAsync(new(instance));
            var fields = before.Fields with
            {
                DisplayName = "Named world",
                Description = "description",
                Notes = "first\nsecond\n",
                Tags = ["Fabric", "Survival", "fabric"],
                Group = "Games",
                Starred = true,
                CustomInfo = "User version text",
                InstanceIsolation = false,
                ModpackProject = "project:42",
                ModpackVersion = "2026.10"
            };
            AssertTrue((await InstanceIdentityService.SaveAsync(new(instance, before.Revision, fields, before.Server))).IsSuccess);
            var after = await InstanceIdentityService.ReadAsync(new(instance));
            AssertTrue(Guid.TryParse(after.Identity, out _)); AssertEqual("Named world", after.Fields.DisplayName);
            AssertEqual("first\nsecond\n", after.Fields.Notes); AssertEqual(2, after.Fields.Tags.Count);
            AssertTrue(after.Fields.Starred); AssertEqual("Games", after.Fields.Group);
            AssertFalse(after.Fields.InstanceIsolation); AssertEqual("project:42", after.Fields.ModpackProject);
            AssertEqual("2026.10", after.Fields.ModpackVersion);
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, before.Revision, fields with { DisplayName = "stale" }, before.Server))).IsSuccess);
            AssertEqual("Named world", (await InstanceIdentityService.ReadAsync(new(instance))).Fields.DisplayName);
            var store = new MinecraftInstanceMetadataStore();
            await store.UpdateAsync(instance, metadata => metadata with { LaunchCount = 7, AuthSettingsLocked = true, ServerLoginRequirement = 1 });
            after = await InstanceIdentityService.ReadAsync(new(instance));
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, after.Revision, after.Fields, after.Server with { LoginRequirement = 0 }))).IsSuccess);
            AssertTrue((await InstanceIdentityService.SaveAsync(new(instance, after.Revision, after.Fields with { Group = "Updated" }, after.Server))).IsSuccess);
            var reopened = await new MinecraftInstanceMetadataStore().LoadAsync(instance);
            AssertEqual(7, reopened.LaunchCount); AssertEqual(after.Identity, reopened.Identity); AssertEqual("Updated", reopened.Group);
            AssertTrue(reopened.AuthSettingsLocked); AssertEqual(1, reopened.ServerLoginRequirement);
            string versionPath = Path.Combine(instance, "identity.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(versionPath))!.AsObject();
            manifest["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.legacyfabric:fabric-loader:0.15.0" });
            await File.WriteAllTextAsync(versionPath, manifest.ToJsonString());
            after = await InstanceIdentityService.ReadAsync(new(instance));
            AssertTrue((await InstanceIdentityService.SaveAsync(new(instance, after.Revision, after.Fields, after.Server with { ExpectedLoader = "Fabric" }))).IsSuccess);
            AssertEqual(0, (await InstanceIdentityService.ReadAsync(new(instance))).EnvironmentMismatches.Count);
            string path = store.GetMetadataPath(instance); await File.WriteAllTextAsync(path, "{ corrupt");
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, "missing", fields, before.Server))).IsSuccess);
            AssertEqual("{ corrupt", await File.ReadAllTextAsync(path));
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, "missing", fields, before.Server), stopped.Token)).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ServerIdentityAndEnvironmentPoliciesNeverBypassMandatoryChecks()
    {
        var offline = new MinecraftLaunchIdentity("offline", "id", "0", MinecraftLaunchIdentityMode.Offline);
        var microsoft = offline with { Mode = MinecraftLaunchIdentityMode.Microsoft };
        var thirdParty = offline with { Mode = MinecraftLaunchIdentityMode.ThirdParty, AuthServer = "https://auth.example.test/api/yggdrasil/" };
        var metadata = new MinecraftInstanceMetadata { ServerLoginRequirement = 2, AuthServerAddress = "https://auth.example.test/api/yggdrasil" };
        AssertEqual(null, MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata, thirdParty));
        AssertTrue(MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata, microsoft) is not null);
        AssertTrue(MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata, offline) is not null);
        AssertTrue(MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata, thirdParty with { AuthServer = "https://other.example.test/api/yggdrasil" }) is not null);
        AssertEqual(null, MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata with { ServerLoginRequirement = 3 }, microsoft));
        AssertTrue(MinecraftServerEnvironmentPolicy.ValidateIdentity(metadata with { ServerLoginRequirement = 0, OfflineLaunchAllowed = false }, offline) is not null);
        AssertEqual(null, MinecraftServerEnvironmentPolicy.NormalizeAuthEndpoint("https://user:password@auth.example.test/"));
        AssertEqual(null, MinecraftServerEnvironmentPolicy.NormalizeAuthEndpoint("http://auth.example.test/"));
        metadata = metadata with { ServerExpectedGameVersion = "1.20.1", ServerExpectedLoader = "Fabric", ServerRequiredMods = ["required_mod"] };
        var inventory = new LaunchModInventory([new("required_mod", "1", "fabric.mod.json", true, new Dictionary<string, string>(), true)], 0, true);
        AssertEqual(0, MinecraftServerEnvironmentPolicy.CompareEnvironment(metadata, "1.20.1", MinecraftModLoaderKind.Fabric, inventory).Count);
        AssertEqual(3, MinecraftServerEnvironmentPolicy.CompareEnvironment(metadata, "1.19.4", MinecraftModLoaderKind.Vanilla, new([], 0, true)).Count);
        AssertEqual(1, MinecraftServerEnvironmentPolicy.CompareEnvironment(metadata, "1.20.1", MinecraftModLoaderKind.Fabric, inventory with { Complete = false }).Count);
        AssertFalse(metadata.IgnoreJavaCompatibility); AssertFalse(metadata.DisableAssetVerification);
    }

    private static async ValueTask InstanceIdentityRejectsNullOwnedFieldsWithoutOverwritingDamagedMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "null-identity", new JsonObject { ["id"] = "null-identity", ["mainClass"] = "fixture.Main" });
            var valid = await InstanceIdentityService.ReadAsync(new(instance));
            var store = new MinecraftInstanceMetadataStore(); string path = store.GetMetadataPath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FoundationHost host = FoundationComposer.Compose(new InMemorySettingsPort(), LauncherDefaults.CreateSchema(),
                new Nexa.Services.Accounts.LaunchProfileFilePort(Path.Combine(root, "profiles.json")));
            await using var processes = new MinecraftProcessService();
            var discovery = new MinecraftInstanceDiscovery(metadataStore: store);
            var coordinator = new MinecraftLaunchCoordinator(root, Path.Combine(root, "runtime"), discovery,
                host.Accounts, host.Settings, new JavaSelectionService(new InMemoryJavaLocator([])),
                new NeverJavaInstaller(), new MinecraftLaunchExecutor(processes));
            foreach (string damaged in new[]
            {
                "{\"schemaVersion\":1,\"logoPath\":null}", "{\"schemaVersion\":1,\"tags\":null}",
                "{\"schemaVersion\":1,\"tags\":[null]}", "{\"schemaVersion\":1,\"serverRequiredMods\":[null]}",
                "{\"schemaVersion\":1,\"authServerAddress\":null}", "{\"schemaVersion\":1,\"description\":null}"
            })
            {
                await File.WriteAllTextAsync(path, damaged);
                bool rejected = false;
                try { await InstanceIdentityService.ReadAsync(new(instance)); }
                catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected);
                AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, valid.Revision, valid.Fields, valid.Server))).IsSuccess);
                var display = await store.LoadForDisplayAsync(instance);
                AssertTrue(display.Error is not null); AssertEqual(0, display.Metadata.Tags.Length);
                var discovered = (await discovery.DiscoverAsync(root)).Single();
                AssertTrue(discovered.MetadataError is not null);
                var launch = await coordinator.PrepareAsync("null-identity", 0);
                AssertFalse(launch.IsSuccess); AssertTrue(launch.Error!.Message.Contains("实例信息不可用", StringComparison.Ordinal));
                bool writeRejected = false;
                try { await store.UpdateAsync(instance, metadata => metadata with { LaunchCount = 3 }); }
                catch (InvalidDataException) { writeRejected = true; }
                AssertTrue(writeRejected);
                writeRejected = false;
                try { await store.SaveAsync(instance, new()); }
                catch (InvalidDataException) { writeRejected = true; }
                AssertTrue(writeRejected);
                AssertEqual(damaged, await File.ReadAllTextAsync(path));
            }
            File.Delete(path);
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, "missing", valid.Fields with { Tags = null! }, valid.Server))).IsSuccess);
            AssertFalse((await InstanceIdentityService.SaveAsync(new(instance, "missing", valid.Fields, valid.Server with { RequiredMods = [null!] }))).IsSuccess);
            AssertFalse(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
