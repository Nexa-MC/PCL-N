using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WorldHealthAndScreenshotTimelineUseScopedActualRoutes()
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
        var newer = new DateTime(2026, 2, 1, 3, 4, 5, DateTimeKind.Utc); var older = newer.AddDays(-1);
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [],
                [new("overview", "总览"), new("game", "游戏设置"), new("saves", "世界", q.InstanceDirectory), new("screenshots", "截图", q.InstanceDirectory)], true, "")
            { Contents = [new("saves", [new("Fixture", true, 100)], true, null), new("screenshots", [new("9999-12-31.png", false, 10) { ModifiedUtcTicks = older.Ticks }, new("1990-01-01.png", false, 10) { ModifiedUtcTicks = newer.Ticks }], true, null)] })));
        InstanceWorldHealthQuery? checkedWorld = null; int checks = 0;
        var pending = new TaskCompletionSource<XsrResult<InstanceWorldHealth>>(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.Register<InstanceWorldHealthQuery, InstanceWorldHealth>(InstanceWorldHealthContract.Read, (q, _) =>
        {
            checkedWorld = q;
            return ++checks == 1 ? ValueTask.FromResult(XsrResult.Success(new InstanceWorldHealth(false, 1, 2, 1, ["fixture incomplete"]))) : new(pending.Task);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Foundation.Commands,
            fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1100, 1500));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1100, 1500)); return condition(); }, TimeSpan.FromSeconds(5)));
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.screenshots"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.screenshots").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "Management.时间线"));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.时间线").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "Management.画廊"));
        AssertTrue(scene.Nodes.Any(node => node.Text == newer.ToLocalTime().ToString("yyyy/MM/dd", System.Globalization.CultureInfo.CurrentCulture)));
        AssertTrue(scene.Nodes.Any(node => node.Text == older.ToLocalTime().ToString("yyyy/MM/dd", System.Globalization.CultureInfo.CurrentCulture)));
        AssertTrue(scene.Nodes.Any(node => node.Text == newer.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.saves").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ManagementContentDetails.Fixture"));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.Fixture").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "Management.检查世界健康"));
        AssertTrue(checkedWorld is null);
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.检查世界健康").Entity);
        Pump(() => scene.Nodes.Any(node => node.Text == "检查不完整，不能确认世界健康。"));
        AssertEqual("instance-A", checkedWorld!.InstanceDirectory); AssertEqual("Fixture", checkedWorld.WorldName);
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.检查世界健康").Entity);
        Pump(() => checks == 2); instance = "instance-B"; Pump(() => settings.SelectedSection == "overview");
        pending.SetResult(XsrResult.Success(new InstanceWorldHealth(true, 2, 3, 0, [])));
        scene = fixture.Shell.Render(new(1100, 1500)); AssertEqual("overview", settings.SelectedSection);
        AssertFalse(scene.Nodes.Any(node => node.Text == "已完成检查，未发现结构损坏。"));
    }
}
