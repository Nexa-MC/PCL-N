using System.Security.Cryptography;
using System.Text;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ContentIntegrityDetailsShowActualFactsAndRetireScopedReads()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-integrity-ui-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(root, "one", "versions", "fixture"), second = Path.Combine(root, "two", "versions", "fixture");
        byte[] payload = Encoding.UTF8.GetBytes("fixture-a");
        foreach (string instance in new[] { first, second })
        {
            Directory.CreateDirectory(Path.Combine(instance, "mods"));
            File.WriteAllBytes(Path.Combine(instance, "mods", "a.jar"), payload); File.WriteAllText(Path.Combine(instance, "mods", "b.jar"), "fixture-b");
        }
        try
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
            string current = first; var queries = new XsrQueryRouterBuilder();
            fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog); fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
            queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery, (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
            queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery, (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
            queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            {
                string mods = Path.Combine(q.InstanceDirectory, "mods");
                var entries = Directory.EnumerateFiles(mods).Select(path => { var info = new FileInfo(path); return new InstanceContentEntry(info.Name, false, info.Length) { ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks, Enabled = true }; }).ToArray();
                return ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [], [new("overview", "总览"), new("game", "游戏设置"), new("mods", "模组", mods)], true, "") { Contents = [new("mods", entries, true, null)] }));
            });
            var late = new TaskCompletionSource<XsrResult<InstanceContentIntegrity>>(TaskCreationOptions.RunContinuationsAsynchronously);
            InstanceContentIntegrity? retired = null; CancellationToken captured = default;
            queries.Register<InstanceContentIntegrityQuery, InstanceContentIntegrity>(InstanceContentIntegrityContract.Read, async (q, ct) =>
            {
                var actual = await InstanceContentIntegrityService.ReadAsync(q, ct);
                if (q.Name == "b.jar") { retired = actual; captured = ct; return await late.Task; }
                return XsrResult.Success(actual);
            });
            var resources = new XsrQueryRouterBuilder();
            resources.Register<ResourceContentOnlineQuery, ResourceContentOnline>(ResourceCatalogContract.ContentOnline, (q, _) => ValueTask.FromResult(XsrResult.Success(
                new ResourceContentOnline(new("expected", "Fixture source", "", "", 1, "https://modrinth.com/project/expected") { Sources = [new(ResourceProvider.Modrinth, "expected")] }, "1", [], null)
                {
                    InstalledFiles = [new(new(ResourceProvider.Modrinth, "expected"), "actual-version", q.Name, Convert.ToHexString(SHA512.HashData(payload)), true),
                        new(new(ResourceProvider.Modrinth, "mismatch"), "wrong-version", q.Name, new string('A', 128), true)]
                })));
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => current);
            settings.ConfigureOnlineContent(resources.Build(new NoopDispatchObserver()), _ => { });
            fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page); var scene = fixture.Shell.Render(new(1100, 3000));
            void Pump(Func<bool> predicate) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1100, 3000)); return predicate(); }, TimeSpan.FromSeconds(5)));
            Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
            Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetails.a.jar"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.a.jar").Entity);
            Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ContentIntegrity.Sha256.Value"));
            AssertEqual(Convert.ToHexString(SHA256.HashData(payload)), FindByKey(fixture.Shell, scene, "ContentIntegrity.Sha256.Value").Text);
            AssertEqual("未知（没有受管理更新基准）", FindByKey(fixture.Shell, scene, "ContentIntegrity.Modified.Value").Text);
            Pump(() => FindByKey(fixture.Shell, scene, "ContentIntegrity.Source.Value").Text == "Modrinth:expected@actual-version");
            AssertFalse(FindByKey(fixture.Shell, scene, "ContentIntegrity.Source.Value").Text!.Contains("mismatch", StringComparison.Ordinal));
            AssertFalse(Directory.Exists(Path.Combine(first, ".nexa-content-updates")));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.返回列表").Entity); scene = fixture.Shell.Render(new(1100, 3000));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.b.jar").Entity);
            Pump(() => retired is not null); current = second; Pump(() => settings.SelectedSection == "overview");
            AssertTrue(captured.IsCancellationRequested); late.SetResult(XsrResult.Success(retired!)); scene = fixture.Shell.Render(new(1100, 3000));
            AssertFalse(scene.Nodes.Any(node => node.Text == retired!.Sha256)); AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ContentIntegrity.Source.Value"));
        }
        finally { Directory.Delete(root, true); }
    }
}
