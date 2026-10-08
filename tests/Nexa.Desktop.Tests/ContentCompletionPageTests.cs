using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ContentBatchUpdatesDispatchOnlySelectedLiveFiles()
    {
        foreach (bool selectedOnly in new[] { true, false })
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
            string instance = "instance-A";
            var queries = new XsrQueryRouterBuilder();
            fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
            fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
            queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
                (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
            queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
                (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
            queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
                ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [],
                    [new("overview", "总览"), new("game", "游戏设置"), new("mods", "模组", q.InstanceDirectory)], true, "")
                { Contents = [new("mods", [new("a.jar", false, 10) { ModifiedUtcTicks = 1, Enabled = true }, new("b.jar.disabled", false, 20) { ModifiedUtcTicks = 2, Enabled = false }], true, null)] })));
            var resources = new XsrQueryRouterBuilder();
            resources.Register<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(ResourceCatalogContract.ContentOnlineBatch, (q, _) =>
                ValueTask.FromResult(XsrResult.Success(new ResourceContentOnlineBatch(q.Files.Select(file =>
                {
                    var version = new ResourceVersion("next-" + file.Name, "Next", "2", "正式版", ["1.21.1"], ["fabric"], "2026-02-01T00:00:00Z", "") { ProjectId = file.Name, File = new("new.jar", "https://cdn.modrinth.com/data/A/new.jar", 10, null, null) };
                    return new ResourceContentOnlineMatch(file, new(new(file.Name, file.Name, "", "", 1, "https://modrinth.com/project/A") { Sources = [new(ResourceProvider.Modrinth, file.Name)] }, "1", [version], null) { UpdateVersion = version, UpdateAvailable = true });
                }).ToArray()))));
            ResourceContentUpdateBatchCommand? updated = null;
            var completion = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commands = new XsrCommandRouterBuilder(); commands.Register<ResourceContentUpdateBatchCommand>(ResourceCatalogContract.UpdateContentBatch,
                async (command, _) => { updated = command; return await completion.Task; });
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Foundation.Commands,
                fixture.Store, fixture.Feedback, () => instance);
            settings.ConfigureOnlineContent(resources.Build(new NoopDispatchObserver()), _ => { }, commands.Build(new NoopDispatchObserver()));
            fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
            var scene = fixture.Shell.Render(new(1100, 900));
            void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1100, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
            Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
            Pump(() => scene.Nodes.Any(node => node.Text?.Contains("已关联 2", StringComparison.Ordinal) == true));
            if (selectedOnly)
            {
                var choice = FindByKey(fixture.Shell, scene, "ContentUpdateSelect.a.jar").Entity;
                Emit(fixture.Intents, "ui.settings.management.action", choice); scene = fixture.Shell.Render(new(1100, 900));
                AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiToggle>(choice)!.IsChecked);
            }
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, selectedOnly ? "Management.更新已选择" : "Management.更新全部已识别").Entity);
            Pump(() => updated is not null); AssertEqual(selectedOnly ? 1 : 2, updated!.Updates.Count);
            AssertTrue(updated.Updates.All(item => item.File.InstanceDirectory == instance && item.File.PageId == "mods"));
            AssertEqual("a.jar", updated.Updates[0].File.Name); AssertEqual(10L, updated.Updates[0].File.ExpectedSize);
            instance = "instance-B"; Pump(() => settings.SelectedSection == "overview");
            completion.SetResult(XsrResult.Success()); scene = fixture.Shell.Render(new(1100, 900));
            AssertEqual("overview", settings.SelectedSection);
        }
    }
}
